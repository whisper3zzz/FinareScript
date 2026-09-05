# UnifiedScript product roadmap

Last reviewed: **2026-09-05**

This document owns current priorities, capability boundaries, and delivery
order. Detailed implementation logs and earlier acceptance results live in
[ROADMAP_HISTORY.md](ROADMAP_HISTORY.md). Existing milestone IDs are retained;
IDs identify scope, not a mandatory numeric sequence. The historical record is
not a second work queue.

## Product goal

Make UnifiedScript as practical to adopt and use as Puerts, with two additional
strengths: C# and Graph edit the same program, and typed generated integration
can outperform the old runtime on measured gameplay workloads.

The normal developer journey is **install -> create a Script Class -> write
gameplay -> Play -> edit/reload -> package**. Reflection discovery, contracts,
bridge generation, and deployment belong behind that workflow. A developer
should not need to maintain a project-specific native facade for a supported API.

This is workflow and Unreal integration parity, not JavaScript compatibility,
unrestricted visual editing of all C#, or a promise to win every benchmark.

## Current execution cursor

- Active milestone: **M13.5 in progress — Puerts comparison and common capability completion**
- Current tranche: **M13.5 — ordinary native object text values**. Puerts' native
  binding example accepts `std::string`, while UnifiedScript's ordinary C++
  object lane still accepts only fixed values and qualified object references.
  Enable practical string fields, constructor/method inputs and returned text
  through generated bindings and a gameplay example, preserving ownership,
  generation lifetime and compatibility of the existing fixed-call prefix.
  Stay in M13.5; this does not start M13.3/M13.4.
- Latest delivered M13.5 checkpoint: **typed reference containers**, following
  reflected interface references (`bffb2d8`) and callbacks (`2827216`).
- Last completed M13.3 slice: **M13.3C-1c-a**, local commit `6c03d51`.
- Container/Soft Reference implementation checkpoint: `46f912d`; the full
  M13.3C-1c-b acceptance scope remains open.
- Current delivery strategy: **M13.5 comparison and capability breadth first ->
  M13.3 deeper Script Class integration -> M13.4 external validation ->
  M13.6 delivery expansion**.
- Transition rule: **usable common gameplay coverage, not full Puerts parity**.
  Compare enough to choose the next missing capability, implement it through the
  public workflow, and return to M13.3/M13.4 once the breadth target below is met.
  One example per family is not sufficient if ordinary use still requires a
  project-specific facade or a missing common call direction.
- Execution focus: **close common Puerts capability gaps, not complete every
  detail of the current Demo or subsystem**. Finish workflow fixes needed to use
  a capability, then select the next practical gap. Full Script Class depth and
  external-project validation remain later milestones.
- Latest capability checkpoint delivered: **basic Actor/ActorComponent networking**.
  Saved generated classes run on one server and two connected clients, including
  a dynamically attached component. Both paths cover RepNotify, explicit RPC,
  ownership rejection and client reload. The component example adds all five
  exposed lifetime conditions and 0/2/3/4-input RPC with int/float/bool/Vector,
  string and World-local object references; the Actor example covers one input.
  Initial notification snapshots survive GC, coalesce and cancel on stop.
  Float/double path transport and runtime-enabled Actor ownership checks are
  repaired. These are real UE network-driver/actor-channel tests, not state copies.
  See the [network recipe](UNREAL_GAMEPLAY_RECIPES.md#replicated-state-and-rpc).
- Latest common-value follow-up: **path and generated-call transport consistency**.
  Integer widths, typed enums and Rotator/Transform now have legacy path codecs,
  including boxed enum arguments and ID-only generated enum results. The connected
  component example verifies enum/Rotator/Transform RPC and a long integer beyond
  double's exact range, including reload. Fallback classification tests cover the
  same codec families. This does not expand every Script Class state/default shape.
- Current UI/event/async checkpoint: **generated UMG and composed gameplay**.
  `AsyncUiDemoScript` creates a real viewport HUD, handles button events, loads a
  class/mesh asynchronously and replaces an owned preview Actor. It includes
  cancellation, duplicate-click suppression, failure/retry and reload/deletion
  cleanup. The example exposed and repaired zero-parameter native event dispatch,
  empty generated dispatch tables and `TEnumAsByte` conversion. See the
  [async UI example](UNREAL_ASYNC_UI_DEMO.md).
- Current Demo delivery checkpoint: **persistent scene, launcher and cooked Win64**.
  One command prepares a saved Script Class/camera/input scene and launches it;
  unchanged preparation preserves the saved files. The same cancel/load/spawn,
  chunk reload and teardown scenario passes in both a separate `-game` process
  and the archived Win64 Development executable using its bundled CoreCLR.
  The UI Automation suite also works with the persistent class present without
  modifying its scene or defaults.
- Current authoring checkpoint: **project-backed C#/Graph publication and live
  Demo reload**. The session evaluates the actual MSBuild project, preserves
  adjacent identities and publishes the preferred runtime chunk transactionally.
  The Demo's preview height changes from 100 to 120 through C#, then to 180 through
  Graph; the same game process reloads and observes height 180. Undo restores the
  original source/chunk. Native Slate loads the full project surface and focuses
  the selected Demo class. The launcher now has an `Author` entry.
- Current daily-authoring checkpoint: **external source refresh and native
  Build & Reload**. Refresh reevaluates project inputs without restarting the
  authoring session. Failed compilation/publication and concurrent saves preserve
  the old loaded debug program and external edits. The actual native coordinator
  rebuilds a C# body, changes a live PIE Actor's result from 100 to 120 and
  refreshes its Graph-only tab. Stale tabs recover; unsaved buffers are retained.
  Attached gameplay builds no longer overwrite loaded shared-host DLLs, and Demo
  staging no longer replaces an existing Graph layout.
- Latest native-binding checkpoint: **generated ordinary C++ object arguments**.
  Constructors and static/instance methods accept pointer, const-pointer and
  mutable/const object-reference inputs; nullable pointers are explicit. C#,
  C++ and node descriptions share that contract. The CoreCLR example composes
  objects without losing alias identity, including reload and failed-candidate
  rollback. Invalid type/scope/null/borrowed-owner inputs are rejected before
  native side effects; old transport prefixes remain usable for fixed calls.
- Latest callback checkpoint: **generated reflected callback arguments,
  synchronous return/ref/out and callback-to-native reentry**. Typed leases,
  native proxies and the public C#/node contract cover immediate and retained
  calls, disposal, GC, reload rollback/replacement and World teardown.
  `CallbackWorkerDemoScript` queues two worker-originated calls; each callback
  reads/writes a generated UE property and performs a nested large-result call
  on the game thread. Both results arrive correctly with exactly two native
  evaluations. Forward invocation lifetime leases avoid the reproduced managed
  lock deadlock; correlated arena retries preserve each call's pending result.
  The ProcessEvent shallow-copy ownership defect is also fixed. See the
  [callback contract](UNREAL_API_CONTRACT.md#reflected-callback-arguments) and
  [implementation record](plans/2026-09-04-reflected-callbacks.md).
  Handler bodies remain C#-only; cooked callback execution, arbitrary native
  `std::function` and every signature combination are not claimed. Choose the
  next practical gap across families, not an exhaustive callback expansion.
- Latest reflected-value checkpoint: **scalar `TScriptInterface` exchange**.
  Properties and function input/return/ref/out preserve the implementing UObject
  through World-local handles; generated interface calls use `Execute_` for both
  native and Blueprint-only implementations. Discovery, selected contracts,
  bindings and node pins retain the exact interface identity. The dedicated
  [interface example](../samples/Unreal/UnifiedScript.Unreal.Demo/InterfaceGameplayDemoScript.cs)
  exchanges the target, invokes its Blueprint implementation, reloads and cleans
  up its World. Invalid type, stale and foreign-World arguments are rejected.
  Managed tests passed (891 total), normal UE Editor compilation and the focused
  InvocationBridge/Blueprint-only CMD Automation passed; regenerated outputs
  match the checked-in artifacts. See the
  [interface contract](UNREAL_API_CONTRACT.md#reflected-interface-references).
  Interface containers, interface-bearing structs/callbacks, Blueprint-defined
  interface types, Script Class interface state/defaults and cooked execution
  remain outside this scalar batch.
- Latest collection checkpoint: **typed object/class and Soft Reference
  containers**. Generated descriptors, input decoding and output encoding now
  share the managed/Blueprint codec's canonical transport identity while native
  storage and node pins retain their exact declared types. `TSubclassOf` inputs
  validate the required base class, including fields inside supported structs.
  The [collection example](../samples/Unreal/UnifiedScript.Unreal.Demo/ReferenceCollectionGameplayDemoScript.cs)
  calls the real `UGameplayStatics::GetAllActorsOfClass`, exchanges Actor arrays,
  Actor-class sets, named Soft Object maps and Soft Class arrays, and refreshes
  from two matching Actors to three after reload. Native and CoreCLR CMD tests
  cover the query, collection exchange, rejection and World cleanup. Existing
  UMG bindings are regenerated for the stricter class input conversion.
  Interface containers and full Script Class state/cook acceptance remain open.
- Remaining workflow scope: installed-consumer authoring and live class-shape/
  default deployment are still open. The latest native test invokes the real
  coordinator, not a physical toolbar click; CMD/NullRHI does not establish visual
  appearance or mouse interaction. Retain these follow-ups without turning Demo
  polish into a prerequisite for the next capability batch. Full M13.3 still waits
  for the M13.5 breadth target.

Existing in-flight work, **M13.3C-1c-b**, is implemented through declaration lowering, typed container
arguments, Blueprint pins and native/managed ABI v3 calls. The five Demo exchange
functions (Array, Set, Map, Soft Object, Soft Class) exercise input, return and
ref/out after generated-class save/reload in CMD Automation. Full slice closure
still needs broader negative/VM coverage and the packaged execution path; this
slice is not complete. Its implementation is retained and can be finished as a
relevant M13.5 capability batch, but it does not force the rest of M13.3 to run
next. Existing integration evidence is in
`artifacts/m13-3c1cb/static-function-dispatch.log`.

## Current capabilities

"Available" below means implemented and previously verified for the reviewed
scope, not universal support for every Unreal type, project, or platform.

| Area | Available today | Important remaining boundary |
| --- | --- | --- |
| Installation and first use — M13.1 | Packaged SDK adoption, Doctor/Repair, clean native and Blueprint-only consumers, first Play and packaged execution | Full environment/module/platform/layout discovery is not closed; broader adoption still needs an external pilot |
| Daily workflow — M13.2 | Native create/open/build entrypoints, dependency-aware Play, compatible all-instance reload, unified diagnostics, IDE setup, one C#/Graph asset | Reviewed fixtures demonstrate the workflow; they are not proof that an arbitrary project is equally easy to use |
| C# / Graph — M0–M8, M12.3 | Structured round-trip editing, stable IDs/comments/layout, semantic review, typed AI proposals | Only the published visual language profile is editable; opaque helper bodies are not graph-editable |
| Managed calls into UE — M6/M7, M13.1B | Reflection-driven discovery and typed bindings for reviewed values, containers, services, assets, SpawnActor, Trace, events and async flows | Calling UE APIs is a different surface from exposing managed methods as generated Blueprint functions; neither implies unlimited signature coverage |
| Reflected interface references — M13.5 | Scalar properties and input/return/ref/out via World-local object handles, native and Blueprint-only `Execute_` calls, exact contract/node identity and a managed reload/cleanup example | Interface containers, interface-bearing structs/callbacks, Blueprint-defined interface types, Script Class interface state/defaults and cooked execution remain open |
| Typed reference containers — M13.5 | Canonical transport for generated object/class/Soft Reference Array, Set and Map values; exact native/node types, subclass validation, real engine Actor query and managed reload/cleanup example | This fixes generated calls into native APIs; interface containers and the full Script Class property/default/cooked matrix remain open |
| Reflected callbacks — M13.5 | Generated typed callback leases, synchronous return/ref/out, native retention and generation/World cleanup; generated UE reentry from two worker-originated calls with correlated result retries | Handler bodies are not graph-editable; callback properties/returns, nested callbacks, ordinary C++ std::function and cooked callback execution remain outside the delivered scope |
| Native extensions — M13.5 | Generated non-reflected UObject members; ordinary C++ constructors/fields/methods, owned/borrowed returns and qualified object inputs, typed wrappers and visual descriptions, automatic native registration and generation/World cleanup | Richer values, ownership transfer/retention, ref/out, inheritance, converters and callbacks remain unfinished; cooked execution and full interactive palette insertion are not established |
| Binary payloads — M13.5 | Generated UObject-member properties, input/return/ref/out using contiguous bytes; owned snapshots, local borrow/slicing, bounded large calls and exact-once output retries, with a CoreCLR lifecycle example | Cross-language transfers copy; reflected state, nested buffers, ordinary-object buffer arguments, callbacks, zero-copy views and cooked/interactive acceptance remain open |
| Composed UI/async gameplay — M13.5 | Generated UMG wrappers/nodes, async loading/spawn/cancel and lifecycle cleanup; persistent scene and cooked runtime checkpoint; project-backed C#/Graph publication, external IDE refresh and native body build/live reload with automatic Slate synchronization | Installed-consumer authoring and live class-shape/default deployment remain open; no physical toolbar/mouse or visual appearance acceptance, and no general UMG/latent parity claim |
| Basic networking — M13.5 | Generated Actor and dynamic ActorComponent replication, all five exposed conditions, int/string RepNotify, mixed RPC including typed enum/Rotator/Transform and wide integers, ownership checks and reload on real server/two-client CMD sessions | Full payload/condition combinations, broader Script Class state/default shapes, push-model verification, travel and cooked networking remain open; this is not packaged WindowsServer support |
| Script Class model — M13.3A/B | UObject, Actor, ActorComponent and FunctionLibrary proxies; reviewed properties/defaults, lifecycle, events, input, delegates and interfaces | General extension behavior, inheritance/default/subobject identity, and component composition remain incomplete |
| Managed functions called by Blueprint — M13.3C-1c | Reviewed common values and `ref`/`out`; container and Soft Reference exchange now passes generated-class save/reload and ProcessEvent | The new container/Soft Reference paths still need broader negative/VM and cooked execution coverage before closing C-1c-b |
| Runtime and Demo — M9–M12 | Compiled chunks, reload, debugging/profiling, packaged BlockBreaker, World scheduling and batch/shared-data routes | The advertised package target is UE 5.8.1 / Win64; cooked networking and broader engine/platform support are not established |
| Performance — M10 | Matched workload infrastructure and measured wins on batch/shared-data routes | Scalar, resource/spawn and reload weaknesses remain; there is no general "faster than Puerts" conclusion |

The project has a working, bounded product and Demo. It is **not yet a general
Puerts replacement**. The earlier M12 RC acceptance is evidence for that release
scope, not completion of the later M13 usability and integration goal.

## Delivery order

Priority decision: **M13.5 -> M13.3 -> M13.4 -> M13.6**. Compare against
Puerts and build sufficient common capability coverage before resuming deeper
class integration and external-project validation. Do not restore numeric order
just because M13.3 or M13.4 still has unfinished work.

M13.5 stays active across multiple capability batches. Completing the reflected
interface batch is a checkpoint within M13.5, not permission to switch to M13.3
or M13.4. Their full scopes remain deferred until the breadth target below is
met; only a concrete dependency of a selected common use case is pulled forward.

M13.5 is an implementation phase guided by comparison, not a documentation-only
audit. Identify practical gaps against Puerts, deliver reusable capability
batches, and keep the comparison current as those batches land. Do not wait for
an exhaustive inventory before implementing a clearly missing common capability.
The selection question is **what ordinary Puerts use case still cannot be
implemented through UnifiedScript's public surface, and what reusable capability
would enable it?** Prioritize those blockers over rare combinations, Demo polish
or completing M13.3/M13.4 in advance. A concrete class prerequisite can be part of
the selected batch; it does not pull forward the rest of those milestones.

Use the comparison table below as the backlog: connect an upstream use case to
the missing reusable capability and a concrete gameplay result. Keep updates in
that table or the relevant existing implementation plan, not a separate audit
document for every batch. After completing the current batch, select the next
high-impact gap across families; do not automatically continue deeper within
the same subsystem or advance to M13.3 because a Demo already runs.

| Order | Deliverable | Why it comes next |
| --- | --- | --- |
| Active now | **M13.5:** compare Puerts and fill common capability gaps | Repeatedly select and deliver substantial, reusable batches from upstream use cases and current gaps |
| Deferred until sufficient breadth | **M13.3:** deepen Script Class integration | Complete extension, inheritance, defaults, subobjects and authoring composition after the common API/runtime surface is usable |
| Deferred until class integration | **M13.4:** one external-project validation | Test the combined product against real project complexity, not discover every basic capability during migration |
| Then | **M13.6:** distribution and platform expansion, broad performance acceptance | Expand the product after capability completion and external validation |

**Neither completion of M13.3 nor M13.4 is a prerequisite for M13.5.** Compare
first, then prioritize frequency of use, missing capability families, technical
dependencies and reusable coverage gains. Likely batches include reflected
API/value/container gaps, non-reflected native bindings, callbacks/async/services,
binary interop and basic replication/RPC. Their exact order follows the gaps
found, not milestone numbering or a fixed signature-by-signature checklist.

**Breadth before depth:** after each usable batch, prioritize a wholly missing
common family or a gap that blocks ordinary gameplay over rarer combinations
in an already usable family. Continue deeper within a family when a concrete
common use case or a dependency requires it. A generated declaration or isolated
runtime primitive is not yet a usable batch: carry the selected behavior through
its public API and a working example before moving on. This applies to native
bindings as well as Script Classes; M13.5 must not become another requirement
to finish one subsystem exhaustively before expanding coverage.

Reuse the existing Script Class foundation. Pull forward a specific M13.3 item
only when a selected capability actually depends on it, and record completion
under its existing ID. Do not make every inheritance, CDO, subobject or composition
case a prerequisite for ordinary bindings, services or networking. The full
M13.3 scope remains later work, not an implicit second active milestone.

Fix reproduced regressions and obvious performance problems during this work;
do not defer every optimization until M13.6. Broad comparative claims and extra
platforms remain later delivery work. Keep existing installation, reload and
packaging operational as capabilities expand.

## Puerts comparison and capability breadth target

The comparison uses the official Unreal documentation for
[reflected APIs and containers](https://github.com/Tencent/puerts/blob/master/doc/unreal/zhcn/script_call_uclass.md),
[native-to-script callbacks](https://github.com/Tencent/puerts/blob/master/doc/unreal/zhcn/engine_call_script.md),
[script-class types and RPC](https://github.com/Tencent/puerts/blob/master/doc/unreal/zhcn/uclass_extends.md),
[mixin](https://github.com/Tencent/puerts/blob/master/doc/unreal/zhcn/mixin.md), and
[non-reflected C++ binding](https://github.com/Tencent/puerts/blob/master/doc/unreal/zhcn/template_binding.md),
reviewed on 2026-09-04. These describe upstream capabilities and restrictions,
not locally reproduced Puerts results. This is an initial family-level backlog,
not an exhaustive signature audit or a claim of parity.

| Capability family | Current UnifiedScript gap or next comparison | Owner |
| --- | --- | --- |
| Reflected API and value types | Scalar `TScriptInterface` properties and input/return/ref/out now work with native and Blueprint-only implementations through generated bindings and exact node descriptions. Containers, interface-bearing structs/callbacks and Blueprint-defined interface types remain separate; continue auditing other common member/reference/discovery forms | M13.1B / M13.3 / M13.5 |
| Containers and Soft References | The typed-reference transport mismatch is closed for generated native calls: Actor arrays, class sets, named Soft Object maps and Soft Class arrays have native/CoreCLR exchange and reload evidence. Generated Blueprint functions retain their earlier initial exchange path; their broader negative/VM/cooked and common state/default acceptance remains open | M13.5; reuse M13.3C-1 work |
| Script classes and extension | Reuse the four proxy kinds and reviewed events; pull forward only dependencies of selected capability batches. Complete deeper extension, inheritance, default subobjects and composition later | M13.3C-2 through C-4, after capability breadth |
| Non-reflected C++ | UObject members and ordinary C++ constructors/fields/methods/owned-borrowed returns plus pointer/reference inputs have generated C#/C++/node descriptions and runtime examples. Next common gap: ordinary-object string fields, constructors, method inputs and results cannot use the fixed-only transport. Richer values, ref/out, retention/transfer, inheritance, callbacks and converters remain separate follow-ups | M13.5 |
| Delegates, interfaces and async gameplay | Generated UMG events and async load/spawn compose in the saved/cooked Demo and daily authoring loop. Reflected callable delegate parameters now support synchronous results/ref/out, native retention and generated UE reentry with a real worker-originated example. Installed-consumer authoring, broader latent/interface/subsystem gaps and richer callback forms remain follow-ups | M13.5 |
| Replication and RPC | Actor and dynamic ActorComponent examples cover the five exposed conditions, RepNotify, mixed/common-value RPC and ownership/reload. Broader combinations and state/default shapes, push-model, travel and cooked coverage remain unfinished | M13.5 |
| Binary and bulk data | World-scoped schema channels and general generated UObject-member byte-buffer exchange now exist. Public copy/borrow/snapshot semantics and a real payload example are implemented; reflected state, nested buffers, native callbacks and zero-copy lifetime contracts remain follow-ups | M13.5; reuse M7 data-plane foundations |
| Daily development and runtime integration | Preserve the working create/build/Play/reload/debug/package path as the families above expand; separate ergonomic gaps from intentional C#/.NET differences | M13.1 / M13.2, with M13.5 ecosystem guidance |

The selected interface use case is grounded in Puerts
[`FInterfacePropertyTranslator`](https://github.com/Tencent/puerts/blob/c526e858e31997a4c816fc531144b78215c989d8/unreal/Puerts/Source/JsEnv/Private/PropertyTranslator.cpp),
reviewed on 2026-09-05. Its property conversion exposes the implementing UObject
and reconstructs the script interface from that object. UnifiedScript now reuses
its World-local object handles and validates the required interface before native
mutation. This comparison guided the scalar value batch; it is not a matched
Puerts runtime result or a requirement to complete all Script Class interface
state, container or callback combinations in the same batch.

The [same pinned upstream revision's native binding example](https://github.com/Tencent/puerts/blob/c526e858e31997a4c816fc531144b78215c989d8/doc/unreal/zhcn/template_binding.md)
includes a `std::string` overload. The next batch addresses that ordinary text
use case in non-UObject bindings; the existing UObject-member string codecs do
not make the separate fixed-only native-object lane support it.

For each family, distinguish **available for the reviewed scope**, **partial**,
**missing**, **not yet audited**, and **intentional difference**. Before declaring
parity, tie the exact behavior to a Puerts version/source example and our
implementation/example. Compare observable semantics and authoring effort, not
method counts, language syntax or benchmark shortcuts. Do not reimplement
capabilities already present, and do not label an unaudited shape as supported.

**Sufficient breadth to resume the full M13.3 scope** means ordinary gameplay
can use the existing class foundation with common reflected calls and members,
collections/Soft References, non-reflected bindings, events/async/services,
binary interop and basic replication/RPC. For the common uses selected from the
comparison, developers must be able to compose these capabilities through public
APIs without per-project handwritten facades. A family is not sufficiently
covered merely because one constructor, signature or happy-path Demo works.

Select those common uses from the Puerts comparison, not only from what our
existing examples already support. A missing everyday operation remains a gap
even when a narrower Demo passes. The switch is based on usable coverage across
these families, not a fixed number of batches, API count or claimed percentage
of Puerts compatibility.

Representative in-repository examples should combine these capabilities in
ordinary gameplay: UI/events and async asset loading/spawn; a non-reflected C++
extension; a binary payload exchange; and a basic client/server interaction.
They must retain the normal build/reload/package workflow. These are usable
scenarios, not a target API count or a requirement to enumerate every signature.
Deeper Blueprint inheritance, CDO/subobject identity and authoring composition
are deliberately not prerequisites for this breadth target.

**Move on when those representative scenarios work through reusable public APIs
and the normal development workflow, and the comparison shows no remaining
blocker to their selected common uses.** Check the ordinary operations, required
call directions and lifecycle behavior, not just whether each family has an
example. Reuse relevant existing verification; this decision does not require
another full-suite run. Use observable gameplay coverage, not an API percentage,
to judge "enough." Declarations or untested stubs alone do not meet this target.

This does not require clearing every M13.5 checkbox, every signature combination
or every Puerts feature. Those checkboxes include broader family backlogs. Carry
uncommon cases, additional platforms and intentional language differences as
explicit follow-ups; do not let them postpone M13.3 indefinitely. Each batch
should close a practical gap rather than restart the comparison audit.

After that breadth checkpoint, complete the remaining M13.3 integration, then
use M13.4 to check external integration and usability. External validation is
still required before claiming a general replacement. Selecting or modifying
an external project requires the user's authorization.

## Remaining work

### M13.5 — Compare Puerts and complete common capability families first

- [ ] **M13.5 — gap-driven capability batches:** refine the family-level
  comparison above against Puerts source/examples and our current implementation.
  Separate available, partial, missing, unaudited and intentional differences;
  choose the next substantial batch by practical impact. Keep the findings here
  and move into implementation once the priority is clear; an exhaustive audit
  is not a prerequisite for starting the first batch.
- [ ] Close common reflected API, value, container and Soft Reference gaps in
  both call directions and normal state usage. Reuse the unfinished M13.3C-1c-b
  work where relevant rather than restarting it or treating it as all of parity.
- [ ] Extend the versioned generation model to common non-reflected C++
  constructors, methods, fields, overloads and inheritance, including callbacks,
  conversion and explicit ownership. Generate C# declarations and visual nodes
  from the same description instead of separate handwritten surfaces.
  The first UObject-member implementation is available; it does not close this
  broader item. Ordinary C++ fixed-value and owned/borrowed-return generation is
  now implemented with an actual generated CoreCLR example, as are qualified
  pointer/reference inputs with exact scope and null policy; richer forms remain
  open. Manual member registration remains only in low-level registry tests.
  See [native extension declarations](UNREAL_API_CONTRACT.md#native-extension-declarations)
  and the [ordinary C++ runtime](UNREAL_API_CONTRACT.md#ordinary-c-object-runtime).
- [ ] Close common latent, delegate, interface, subsystem, UI and asset-loading
  gaps through the existing automatic surface. Reuse the reviewed forms already
  implemented and compose them in representative gameplay examples.
  The composed UMG/async/SpawnActor runtime checkpoint is implemented, including
  cancel/retry, saved-class reload and teardown. Persistent launch and cooked
  execution are verified. Project-backed C#/Graph transactions now change the
  live Demo through chunk reload; external refresh and the native body-build/live
  reload path are verified. Installed-consumer authoring and class-shape/default
  deployment remain open, as described by the current cursor.
  Reflected callbacks now have generated typed leases and synchronous return/ref/out,
  including retained native calls and worker-originated reentry. This closes that
  callback batch, not all latent/interface/subsystem or native callback support.
- [ ] Audit and complete bounded byte-buffer interop for ordinary binary payloads,
  with explicit copy/borrow/ownership rules and generation-safe callbacks. Reuse
  existing transports and data-plane contracts where applicable.
  The public contiguous-buffer batch is implemented and verified for generated
  UObject members, including large ref/out calls and runtime lifecycle. Native
  callbacks, reflected state and richer compositions remain open; this broader
  item does not block moving to the next missing capability family.
- [ ] Add Script Class replication/RPC/RepNotify and authority/lifetime semantics
  as core capability work. Prove a basic client/server example on the existing
  class foundation; complete specific missing class dependencies as needed rather
  than waiting for all of M13.3. Packaged WindowsServer remains unsupported until
  separately built, packaged and exercised.
  Actor and ActorComponent checkpoints include public samples and server/two-client
  CMD tests: initial notification buffering, native class save/reload, dynamic
  components, exposed conditions, mixed-payload sends and client generation
  replacement. This does not close the remaining network scope or prove every
  accepted RPC signature. The common path codecs now include integer widths,
  enums and core transforms; next compose other common gameplay families.
- [x] Publish usable .NET library, async/threading and native-callback guidance,
  with explicit runtime/platform restrictions. Do not imply npm/Node ecosystem
  compatibility or unrestricted dynamic-language behavior.
  The [gameplay recipes](UNREAL_GAMEPLAY_RECIPES.md#net-libraries-and-reload)
  now cover MSBuild runtime copies, chunk dependency verification, reload
  ownership, World-scoped async work and the worker-originated callback route.
  Native packages, source-generator replay and additional execution platforms
  remain outside the documented runtime support.

**Done when:** the capability breadth target above is met through reusable
contracts and working examples. Unsupported edge cases have clear diagnostics;
rejecting a planned common capability does not count as implementing it. Neither
full M13.3 completion nor external-project proof is required to complete M13.5.

### M13.3 — Make Script Classes ordinary Unreal gameplay types

Resume this full scope after M13.5 reaches the capability breadth target. A
specific prerequisite may be implemented earlier inside a M13.5 batch without
pulling forward the entire milestone.

The reviewed four-kind class foundation, common property/default shapes,
lifecycle/input/event/delegate/interface routes, and common-value plus
`ref`/`out` Blueprint functions are already implemented. Continue from them.

- [ ] **M13.3C-1c-b — container and Soft Reference functions:** carry reviewed
  array/set/map and Soft Object/Class Reference shapes through lowering,
  Declaration IR, generated pins, native/managed codecs and dispatch. Cover
  input, return and supported `ref`/`out` directions. Unsupported combinations
  must produce actionable diagnostics, not silent type substitution or facades.
- [ ] **M13.3C-2 — explicit extension behavior:** define how managed behavior
  extends existing BlueprintNativeEvent and BlueprintImplementableEvent paths,
  with one owner, explicit parent-call semantics and predictable reload. Build
  on the existing event/super-call support; do not introduce prototype mutation.
- [ ] **M13.3C-3 — inheritance, defaults and subobject identity:** close the
  remaining class/CDO/subobject cases across compile, reload, cook/package,
  Blueprint inheritance and multi-World use. Extend the existing asset-operation
  transactions when those identities participate in rename, duplicate or redirect.
- [ ] **M13.3C-4 — class composition in normal authoring:** add the missing
  base-class changes, component composition and related default/reference editing.
  Reuse the completed M13.2 rename/duplicate/delete and recovery workflow rather
  than scheduling those operations again as new capabilities.

The broader M13.3C-1 state/property matrix remains bounded. Audit container and
Soft Reference property/default shapes against common Puerts usage and close the
missing common forms before the pilot; finishing the function slice does not
imply that all state shapes are complete.

**Container/Soft Reference slice completion:** a checked-in managed gameplay/library example exposes
all three container families and both Soft Reference families through generated
Blueprint nodes. Focused tests cover values, supported directions and rejected
combinations; the relevant reload and packaged execution path proves that the
bindings survive beyond an editor-only call.

**Milestone outcome:** a developer can use a generated managed gameplay class in
the reviewed Blueprint inheritance/composition workflow, preserve intentional
defaults and references, reload it safely and package it without a handwritten
proxy class or a second implementation of its behavior.

### M13.4 — Validate the completed capability level in another real project

Start after the M13.5 capability breadth target and the remaining M13.3 class
integration, not as the discovery prerequisite for either. This stage tests how
the broadly usable capabilities compose in practice.

- [ ] Select an authorized non-BlockBreaker project and one representative
  gameplay slice. `D:\Project\LyraStarterGame` is only a candidate, not a selected
  target or permission to modify it.
- [ ] Install through the shipped SDK workflow, inventory the slice's actual
  Puerts/Blueprint/C++ boundaries and migrate its required behavior using generated
  APIs. Classify unsupported dependencies before promising full migration.
- [ ] Exercise real UI/event/data binding and asset/Soft Reference/async loading,
  plus at least one system-heavy domain required by the project: GAS, Animation,
  AI/Navigation or networking. Do not require all four merely to start validation.
- [ ] Compare the original and migrated observable behavior, then reproduce the
  documented workflow on a second checkout. If the original uses Puerts, run a
  matched dual-runtime comparison; otherwise do not invent a Puerts baseline.

**Done when:** the chosen slice is usable through the installed product without
maintainer-only commands or project-specific facade patches. Remove its old
behavior owner only when equivalence and the authorized scope permit it. New
integration defects feed back into the reusable generators/runtime; a missing
required dependency still blocks acceptance rather than being hidden by a facade.

### M13.6 — Expand delivery and improve measured weak spots

- [ ] Maintain and publish the existing SDK/distribution workflow, supported
  versions, upgrade guidance and issue-reporting path as a consumer-facing product.
  Reuse the installation, rollback and diagnostic tools already implemented.
- [ ] Reproduce an external install/upgrade/rollback cycle with useful opt-in,
  privacy-bounded diagnostics for real installation or runtime failures.
- [ ] Add one consumer-selected UE/platform combination and prove its build,
  package and runtime behavior before advertising it as supported.
- [ ] Optimize reproduced scalar, resource/spawn and reload weaknesses without
  weakening object ownership, rollback, state migration or World teardown.
  Keep CoreCLR generation replacement and Puerts module-cache reexecution
  explicitly distinguished in reload comparisons.
- [ ] Rerun affected matched workloads after optimizations. Run the complete
  matched workload and package-size suite for a broad performance claim or a
  newly advertised platform, not for unrelated documentation edits.

**Done when:** every advertised configuration can install, run, upgrade,
diagnose and ship through documented steps, and performance claims are supported
by reproducible matched workloads on that configuration.

## What is not a new work item

- M0–M12 implementation history is not an active backlog. Reuse the existing
  compiler, authoring, bridge, scheduling, debug and delivery foundations.
- Early unchecked language items can be superseded by later work. Use the
  current [visual language profile](VISUAL_CSHARP_PROFILE.md) and
  [foreach support](FOREACH_LOOPS.md), not historical M4 checkboxes, to decide
  what is supported. Unrestricted control-wire rewiring, mutable-ref iteration
  and borrowed async iteration are not implicit promises.
- A new VM, wholesale shared-memory conversion, another general editor rewrite
  or new gameplay-specific facades are not planned prerequisites without a
  demonstrated consumer or performance need.
- Harness work supports delivery. It is not a substitute product milestone or
  a reason to turn every small change into full release acceptance.

## Working and verification policy

For an unqualified request to continue, use the current execution cursor. An
explicit user request takes precedence. Finish a meaningful behavior slice;
do not proliferate tiny roadmap IDs simply to report more completed stages.

Follow [AGENTS.md](../AGENTS.md): use relevant tests and necessary builds for the
behavior changed. Prefer CMD/NullRHI for repeatable UE checks; use interactive
Editor checks for visual appearance and interaction. Harness profiles are
optional bundles, not mandatory steps. Packaging, broad runtime suites and
performance comparisons belong where the affected behavior or claim needs them.

Update this document when capability, priority or completion changes. Keep
detailed commands and results in the relevant technical document or generated
report. Do not copy long execution transcripts into the live roadmap or revive
the historical cursor. Formal release requirements remain in
[RELEASE_READINESS.md](RELEASE_READINESS.md).

## Evidence and technical references

The reflected-callback batch passes 187 affected managed tests, including
callback-only type dependency closure, node signatures, old ABI prefixes,
cross-thread reentry and disposal during a pending result retry:
`artifacts/m13-5-callbacks/managed/callback-contracts-final.trx`.
The UE 5.8.1 Editor build and Demo chunk emission pass. Six CMD cases pass in
`artifacts/m13-5-callbacks/callback-reentry-native.log`: three callback cases,
correlated-result isolation/bounds/cleanup, native-object ManagedLifetime and
binary ManagedRoundTrip. That run exposed an outdated 80-byte table assertion
in NativeTransport.Abi; after checking the new 96-byte suffix and old offsets,
the ABI case passes in `abi-correlated-verified.log` under the same directory.
WorldHost.Lifecycle and LifecycleStress also pass in `world-lifetime-verified.log`,
including zero leaked native contexts and pending worker calls after teardown.
These are focused integration results, not new cooked, physical UI or performance
acceptance. Detailed lifecycle and retry decisions are in the callback plan.

The ordinary-object argument checkpoint passes 57 focused managed tests in
`artifacts/m13-5-native-arguments/managed/object-arguments.trx` and 39 affected
generation/contract/ABI tests in `managed/affected-generation.trx` under that
directory, including checked-in managed/native generator output matching.
The UE 5.8.1 Editor build and Demo chunk emission succeed. All six selected CMD
tests pass in `artifacts/m13-5-native-arguments/native-arguments.log`, including
generated object arguments, old ownership/lifecycle behavior and rejection of
native-object tags by UObject transports. The actual CoreCLR example covers
constructor/static/instance inputs, alias mutation, explicit null, failed reload
and generation/World cleanup. No cooked, interactive palette or comparative
performance result is claimed by this batch.

The external-refresh/native-build checkpoint passes 31 focused managed tests in
`artifacts/m13-5-ui/authoring-tests/project-refresh-final.trx`, including project
membership/settings changes, concurrent saves, failed publication and retention
of the exact old loaded debug program. UE 5.8.1 builds and configured CMD checks
pass in `artifacts/m13-5-ui/authoring-tests/onboarding-live-build-verified.json`.
The opt-in native project test separately passes with its real live fixture in
`artifacts/ue/async-authoring/20260904-190743-946/runtime.log`: native Build & Reload
changes the existing PIE Actor from 100 to 120 and refreshes the Graph-only tab.
The earlier failed attempt exposed shared-host DLL copying into a live Editor;
the final run skips host staging and successfully rebuilds gameplay/chunks.
The external-save/Graph/live-game 100-to-180 path passes in
`artifacts/ue/async-authoring/20260904-184826-724/runtime.log`. Both drivers undo
their edits. A direct `StageUnrealManagedDemo` check preserves an existing layout.
These are not new installed-consumer, class-shape, cooked or physical UI results.

The project-backed authoring checkpoint passes 88 focused managed tests in
`artifacts/m13-5-ui/authoring-tests/project-authoring.trx`, the UE 5.8.1 Editor
build and four CMD tests in `artifacts/m13-5-ui/authoring-tests/native-authoring.log`.
That native run supplies the emitted full-project surface through
`-UnifiedScriptProjectSurface` and verifies its parser, class selection and
editable height Graph, alongside the three UI tests. The same-process source
edit, Graph apply and changed runtime height pass in
`artifacts/ue/async-authoring/20260904-182118-640/runtime.log`; the test undoes its
edits through the authoring API. This does not establish physical interaction,
the complete native daily-build path or a new cooked-source-edit result.

The persistent UI Demo builds for Editor and Win64 Development. The saved scene
passes the actual standalone game path in
`artifacts/ue/async-demo/20260904-174027-825/runtime.log`; repeated preparation
leaves both saved class and map unchanged. All three UI CMD tests pass with the
persistent class present in `artifacts/m13-5-ui/ui-persistent-demo.log`.
BuildCookRun and the archived executable pass; the final runtime result is
`artifacts/ue/async-demo/20260904-174236-470/packaged-runtime.log`, including the
package-local CoreCLR path. The generated package contains `StartAsyncDemo.cmd`.
These checks prove cancel/spawn/chunk reload/teardown beyond PIE, not source-change
reload, Graph apply, rendered appearance or physical mouse input. No unrelated
managed/full-suite rerun or new performance comparison is claimed.

The composed UI runtime checkpoint passes the UE 5.8.1 Editor build, Demo
chunk emission and 39 relevant managed tests, including generated C#/C++ output
matching (`artifacts/m13-5-ui/managed/ui-contracts-final.trx`). The final
`UI.AsyncGameplay` CMD result is
`artifacts/m13-5-ui/ui-view-lifetime-final.log`. Five additional affected CMD
tests passed in `artifacts/m13-5-ui/ui-verified.log`: `UI.EmptyDelegateParameters`,
`UI.GeneratedByteEnum`, `NativeTransport.Abi`, `WorldHost.Lifecycle` and
`WorldHost.LifecycleStress`. That earlier run's UI gameplay assertion assumed a
canceled load could not already have a queued native completion; the final test
instead proves that Tick drains it without spawning or changing canceled UI
state. Its final run also covers reload during loading, retired-view GC and
Actor deletion. This is not installed authoring, cooked or visual acceptance.

The common-value transport follow-up passes the UE 5.8.1 Editor build, Demo
chunk emission, 85 relevant managed tests (including fallback classification)
and the three native ABI/Actor/ActorComponent CMD tests. Evidence is in
`artifacts/m13-5-values/managed/common-values-contracts-final.trx` and
`artifacts/m13-5-values/common-values-verified.log`. Native codec tests cover
integer limits, enum identity, owned payloads and rejected writes; the connected
example proves enum/Rotator/Transform RPC and exact long state across reload.
No full-suite rerun, cooked result or performance claim is implied.

The ActorComponent/mixed-payload checkpoint passes the UE 5.8.1 Editor build and
the three affected CMD tests (`NativeTransport.Abi`, `Networking.ConnectedComponent`,
`Networking.ConnectedScriptClass`) in `artifacts/m13-5-network/components-verified.log`.
Its 42 managed networking/transport tests pass in `managed/component-transport-contracts.trx`
under the same directory, and Demo chunk emission succeeds. This is the final
focused result after repairing float/double path transport and runtime-enabled
Actor identity checks; earlier full-suite results below predate these edits.
The five conditions are None, InitialOnly, OwnerOnly, SkipOwner and InitialOrOwner
on the tested component, not every Unreal replication condition or combination.

The first networking Actor checkpoint passes the UE 5.8.1 Editor build and all
38 UE CMD tests, including `Networking.ConnectedScriptClass`
(`artifacts/m13-5-network/unreal-suite.log`). The complete managed run passes 784
tests (`managed/managed-complete.trx` under the same directory). Later RPC payload
rejection cases and state-patch preservation pass in the 28 focused cases in
`managed/networking-contracts-final.trx`. After the initial notification GC fix
and stricter previous-value pin validation, the affected native code rebuilds
and the focused networking test passes in `networking-verified.log`. That run
covers snapshot ownership across GC, coalescing, cancellation on stop, and the
connected Actor scenario. The earlier full-suite result is not a rerun of these
later edits. Demo chunk emission and repository validation pass. The connected
run uses in-process UE server/client Worlds with
real loopback connections, not packaged executables or manually copied state.
No networking performance or full Puerts parity claim is made.

The binary payload batch passes the UE 5.8.1 Editor build, all 770 managed tests
(including 26 buffer tests), and all 37 UE CMD tests, including both
`BinaryBuffers` tests. Evidence: `artifacts/m13-5-binary/managed/managed-complete.trx`
and `artifacts/m13-5-binary/unreal-suite.log`. A later test-only addition verifies
generated buffer calls in Graph; all 26 focused cases pass again in
`managed/buffers-graph-final.trx`. Demo chunk emission succeeds;
`generation/deterministic-generation.json` under the same artifact directory
matches all nine checked outputs. Repository validation passes after the CMD
suite finishes. No cooked binary-sample result, full interactive insertion or
Puerts performance improvement is claimed.

The generated ordinary C++ batch passes the UE 5.8.1 Editor build, 744 managed
tests and 35 UE CMD tests (`artifacts/m13-5-native-generated/managed/managed-complete.trx`
and `artifacts/m13-5-native-generated/unreal-suite.log`). After that full run,
a duplicate-owned-return rejection fix was added to the generated native path;
the final affected rerun passes 23 managed native-object tests and all four
native-object CMD tests (`managed/native-objects-final.trx` and
`native-objects-final.log` under that artifact directory), with a successful UE
rebuild. Final deterministic generation matches all nine tracked outputs in
`generation-final/deterministic-generation.json`. The semantic test projects
construction, child access, field mutation and disposal to Graph, and the Demo
chunk emits successfully. No interactive palette, cooked execution or new
Puerts performance result is claimed by this batch.

The ordinary C++ runtime checkpoint has a UE 5.8.1 Editor build, 729 passing
managed tests, and 34 passing UE CMD tests. The three `NativeObjects` tests cover
ownership, ABI validation and real CoreCLR lifecycle, including failed startup,
candidate rollback, retained old handles, generation replacement, forgotten
Dispose and World detach. Ownership tests also exercise a 10,000-level borrowed
chain and 1,000 independent owned objects; these are correctness tests, not a
performance comparison. Evidence:
`artifacts/m13-5-native-objects/managed/managed-complete.trx` and
`artifacts/m13-5-native-objects/unreal-suite.log`. Demo chunk emission succeeds;
this older runtime-only result did not establish generated authoring or cooked
execution. Generated authoring is covered by the newer batch above.

The M13.5 native UObject-member batch has an Editor build, the complete managed
suite (714 tests), and the complete UE CMD suite (31 tests), including the two
new native-extension tests and existing Blueprint container/Soft Reference
dispatch. Evidence: `artifacts/m13-5-native/managed/managed-complete.trx` and
`artifacts/m13-5-native/unreal-suite.log`. These results do not establish
ordinary C++ object support, cooked execution or a new performance comparison.

The last completed product slice is recorded in the
[M13.3 historical record](ROADMAP_HISTORY.md#m133--managed-script-class-and-blueprint-parity)
and commit `6c03d51`. Its existing packaged execution report is
`artifacts/m13-3c1c/golden-path-final/script-class-golden-path-report.json`.
That report is historical evidence, not a fresh result for the current worktree.

- Getting started: [first project](UNREAL_FIRST_PROJECT.md),
  [gameplay recipes](UNREAL_GAMEPLAY_RECIPES.md).
- Class and binding contracts: [Declaration IR](DECLARATION_IR.md),
  [Unreal adapter](UNREAL_ADAPTER.md), [API contract](UNREAL_API_CONTRACT.md).
- Dual authoring: [visual language profile](VISUAL_CSHARP_PROFILE.md),
  [BlockBreaker round-trip](BLOCKBREAKER_ROUND_TRIP.md).
- Comparative evidence: [workload suite](PERFORMANCE_WORKLOAD_SUITE.md),
  [Unreal performance](UNREAL_PERFORMANCE.md).
- Engineering context: [architecture](ARCHITECTURE.md), [Harness](HARNESS.md),
  [milestone history](ROADMAP_HISTORY.md).
