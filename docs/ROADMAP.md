# UnifiedScript product roadmap

Last reviewed: **2026-09-04**

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
- Current tranche: **M13.5 — gap-driven capability batches**
- Last completed product slice: **M13.3C-1c-a**, local commit `6c03d51`.
- Container/Soft Reference implementation checkpoint: `46f912d`; the full
  M13.3C-1c-b acceptance scope remains open.
- Current delivery strategy: **M13.5 comparison and capability breadth first ->
  M13.3 deeper Script Class integration -> M13.4 external validation ->
  M13.6 delivery expansion**.
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
- Next capability batch: **common-value consistency across path and generated
  calls**. The path transport still lacks other integer widths, enum and
  Rotator/Transform codecs available through generated ABI v3. Close these common
  state/RPC/fallback gaps through public APIs and examples, and align fallback
  classification with what the runtime can actually carry. Do not confuse an
  accepted declaration with a working send. Push-model, travel, cooked networking
  and richer binary/native-object signatures remain explicit follow-ups; do not
  turn them into prerequisites for all other M13.5 families or resume all M13.3.

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
| Native extensions — M13.5 | Generated non-reflected UObject members; ordinary C++ constructors/fields/methods and owned/borrowed returns, typed wrappers and visual descriptions, automatic native registration and generation/World cleanup | Ordinary-object arguments, richer signatures, inheritance, converters and callbacks remain unfinished; cooked execution and full interactive palette insertion are not established |
| Binary payloads — M13.5 | Generated UObject-member properties, input/return/ref/out using contiguous bytes; owned snapshots, local borrow/slicing, bounded large calls and exact-once output retries, with a CoreCLR lifecycle example | Cross-language transfers copy; reflected state, nested buffers, ordinary-object buffer arguments, callbacks, zero-copy views and cooked/interactive acceptance remain open |
| Basic networking — M13.5 | Generated Actor and dynamic ActorComponent replication, all five exposed conditions, int/string RepNotify old values, explicit Server/Client/Multicast RPC, mixed input payloads, ownership checks and reload on real server/two-client CMD sessions | Other path-transport value kinds, full payload/condition combinations, push-model verification, travel and cooked networking remain open; this is not packaged WindowsServer support |
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

| Order | Deliverable | Why it comes next |
| --- | --- | --- |
| First | **M13.5:** compare Puerts and fill common capability gaps | Use upstream examples and current source to choose substantial, reusable implementation batches |
| After sufficient breadth | **M13.3:** deepen Script Class integration | Complete extension, inheritance, defaults, subobjects and authoring composition after the common API/runtime surface is usable |
| After class integration | **M13.4:** one external-project validation | Test the combined product against real project complexity, not discover every basic capability during migration |
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
[script-class types and RPC](https://github.com/Tencent/puerts/blob/master/doc/unreal/zhcn/uclass_extends.md),
[mixin](https://github.com/Tencent/puerts/blob/master/doc/unreal/zhcn/mixin.md), and
[non-reflected C++ binding](https://github.com/Tencent/puerts/blob/master/doc/unreal/zhcn/template_binding.md),
reviewed on 2026-09-04. These describe upstream capabilities and restrictions,
not locally reproduced Puerts results. This is an initial family-level backlog,
not an exhaustive signature audit or a claim of parity.

| Capability family | Current UnifiedScript gap or next comparison | Owner |
| --- | --- | --- |
| Reflected API and value types | On-demand generation exists; audit common native/Blueprint member forms, references, defaults and discovery gaps beyond the existing fixtures | M13.1B / M13.3 / M13.5 |
| Containers and Soft References | Generated Blueprint functions now have the initial native/managed exchange path; compare both call directions and common state shapes, then complete the missing paths needed by everyday code | M13.5; reuse M13.3C-1 work |
| Script classes and extension | Reuse the four proxy kinds and reviewed events; pull forward only dependencies of selected capability batches. Complete deeper extension, inheritance, default subobjects and composition later | M13.3C-2 through C-4, after capability breadth |
| Non-reflected C++ | UObject members and ordinary C++ constructors/fields/methods/owned-borrowed returns have generated C#/C++/node descriptions and runtime examples. Ordinary-object arguments, rich signatures, inheritance, callbacks and converters remain partial/missing; do not exhaust this family before expanding breadth | M13.5 |
| Delegates, interfaces and async gameplay | Reviewed routes exist; audit and fill remaining common callback, latent, UI, subsystem and asset-loading forms with reusable examples | M13.5 |
| Replication and RPC | Actor and dynamic ActorComponent examples cover the five exposed conditions, RepNotify, mixed-payload RPC and ownership/reload. Common path-value gaps, broader combinations, push-model, travel and cooked coverage remain unfinished | M13.5 |
| Binary and bulk data | World-scoped schema channels and general generated UObject-member byte-buffer exchange now exist. Public copy/borrow/snapshot semantics and a real payload example are implemented; reflected state, nested buffers, native callbacks and zero-copy lifetime contracts remain follow-ups | M13.5; reuse M7 data-plane foundations |
| Daily development and runtime integration | Preserve the working create/build/Play/reload/debug/package path as the families above expand; separate ergonomic gaps from intentional C#/.NET differences | M13.1 / M13.2, with M13.5 ecosystem guidance |

For each family, distinguish **available for the reviewed scope**, **partial**,
**missing**, **not yet audited**, and **intentional difference**. Before declaring
parity, tie the exact behavior to a Puerts version/source example and our
implementation/example. Compare observable semantics and authoring effort, not
method counts, language syntax or benchmark shortcuts. Do not reimplement
capabilities already present, and do not label an unaudited shape as supported.

**Sufficient breadth to resume the full M13.3 scope** means ordinary gameplay
can use the existing class foundation with common reflected calls and members,
collections/Soft References, non-reflected bindings, events/async/services,
binary interop and basic replication/RPC. No common family in this scope should
remain wholly missing or depend on a per-project handwritten facade.

Representative in-repository examples should combine these capabilities in
ordinary gameplay: UI/events and async asset loading/spawn; a non-reflected C++
extension; a binary payload exchange; and a basic client/server interaction.
They must retain the normal build/reload/package workflow. These are usable
scenarios, not a target API count or a requirement to enumerate every signature.
Deeper Blueprint inheritance, CDO/subobject identity and authoring composition
are deliberately not prerequisites for this breadth target.

Use those scenarios to decide when to return to M13.3, not a percentage of
Puerts APIs or completion of every item within each family. The transition does
not require clearing every M13.5 checkbox: those items describe broader family
backlogs, not an exhaustive prerequisite for M13.3. Once the representative
scenarios' common paths work through reusable product APIs, carry the remaining
uncommon cases as explicit follow-up work and resume M13.3, then M13.4. Each
batch should make the next missing everyday capability clearer, not reset the
comparison audit.

This does not require every Unreal API or every combination of signatures.
Rare combinations, platform additions and language-specific differences remain
explicitly bounded. However, an entire common family that only has declarations
or an untested stub is not sufficient coverage. After this target, complete the
remaining M13.3 integration and then use M13.4 to check external integration and
usability. External validation is still required before claiming a general
replacement. Selecting or modifying an external project requires the user's
authorization.

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
  now implemented with an actual generated CoreCLR example; richer forms remain
  open. Manual member registration remains only in low-level registry tests.
  See [native extension declarations](UNREAL_API_CONTRACT.md#native-extension-declarations)
  and the [ordinary C++ runtime](UNREAL_API_CONTRACT.md#ordinary-c-object-runtime).
- [ ] Close common latent, delegate, interface, subsystem, UI and asset-loading
  gaps through the existing automatic surface. Reuse the reviewed forms already
  implemented and compose them in representative gameplay examples.
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
  accepted RPC signature; path-codec gaps are the next common-capability batch.
- [ ] Publish usable .NET library, async/threading and native-callback guidance,
  with explicit runtime/platform restrictions. Do not imply npm/Node ecosystem
  compatibility or unrestricted dynamic-language behavior.

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
