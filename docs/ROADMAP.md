# Finare product roadmap

Last reviewed: **2026-09-15**

This document owns the active product outcome, remaining work and completion
conditions. [CAPABILITIES.md](CAPABILITIES.md) owns implemented scope and limits.
[History](history/README.md) retains previous milestones and verification logs.
Reorganizing documentation or tests does not complete a product milestone.

## Product goal

Make the normal developer journey reliable:
**install -> create a Script Class -> write gameplay -> Play -> edit/reload -> package**.
C# and Graph must edit the same program. Supported Unreal APIs must work through
the public generated surface without a project-specific native facade.

Puerts comparison helps identify common workflow gaps. It does not imply
JavaScript compatibility, unrestricted visual C#, complete API parity or a
performance win on every workload.

## Current execution cursor

- Active milestone: **M13.3 — ordinary Script Class integration**.
- Current tranche: **M13.3C-4** — normal C# use of UE component creation,
  attachment and lifecycle APIs, using the object/type foundation below.
- [x] Unify generated C# object types, native inheritance, checked object views,
  and World-scoped identity through the existing invocation transport.
- [x] Decouple managed behavior ownership from the native parent class,
  including activation, callbacks, state, reload and destruction.
- [x] Converge Script Class reflected properties/containers on generated
  native accessors and value-snapshot semantics; remove the old managed
  mirror synchronization path and require partial reflected state.
- Architecture decisions and unfinished work:
  [unified object model](plans/2026-09-13-unified-unreal-object-model.md).
- Verification scope: **Editor-only for current work**. Additional Cook/package
  checks are deferred; existing cooked results retain their original scope.
- Integration reference: the already authorized **Lyra health/HUD pilot**.
  Its passed Editor sequence informs affected regression selection while class
  work continues. A new external project still needs its own authorized scope.

The explicit user task takes precedence over this cursor. For an unqualified
request to continue product iteration, take the next unfinished behavior below.

## Current capabilities

The selected M13.5 common capability breadth checkpoint is delivered. Blueprint
function exchange (C-1c-b), event extension (C-2), and reviewed inheritance,
defaults and identity workflows (C-3) have recorded verification. Their exact
boundaries are in [capabilities](CAPABILITIES.md), not inferred from a checked box.

C-4 already has declaration/base edits, partial-source ownership, reserved
managed candidates, native publication/recovery, exact function-owner routing
and stopped-hierarchy preservation. The Actor + Health/Mover backend fixture
also runs through saved/reopened classes, scalar component references and
save-triggered batch reload in Editor. Native test code assembles its SCS nodes
and installs an isolated build command; this does not complete the normal C#
authoring workflow. The remaining C-4 composition/ownership operations are open.

## Delivery order

| Order | User-visible outcome | Completion boundary |
| --- | --- | --- |
| Delivered foundation: object/type integration | Use strongly typed UE objects, extend supported native parents without a required Finare base, and access reflected state with explicit ownership | Three stages above verified through public bindings and affected Editor checks |
| Active: M13.3C-4 | Edit ordinary Actor/component composition through normal authoring while preserving intentional defaults and references | Complete the behavior and Editor checks below using the unified integration |
| Alongside affected changes: M13.4 | Keep the installed Lyra health/HUD workflow usable and close its remaining asset/async coverage | Run relevant public-workflow checks when changed code affects the pilot |
| Later: M13.6 | Ship a clearly identified candidate and extend supported configurations | Explicit release, upgrade and platform acceptance |

External feedback is already part of current development. Do not describe Lyra
as waiting to start, or require every class/API combination before using that
feedback. Conversely, a passing health/HUD slice does not finish all of M13.4.

## Remaining work

### M13.3 — Make Script Classes ordinary Unreal gameplay types

- [ ] **M13.3C-4** Complete the remaining composition/default/reference ownership
  workflows described below, using the delivered object/type integration.

The first composition backend fixture executes C# gameplay with explicit ordinary
`AActor`, two Finare component classes, authored defaults/overrides, a same-Actor
component reference, save/reopen, and source-save compilation/reload with all
three prior generations retained when one candidate fails. The
[reproduction guide](../tests/Finare.Tests/Fixtures/ClassComposition/README.md)
defines the Editor fixture and its isolated build-command substitution.

The runtime entry uses UE's component operations through the available
generated API library. It does not introduce a Finare composition schema,
component member list or per-native-family execution adapter. Native test code
must observe the components created by C#, rather than assemble them for it.

The runtime entry is now demonstrated by the
[C# component creation example](../tests/Finare.Tests/Fixtures/NativeComponentCreation/README.md):
an ordinary Actor creates a mesh and two ordinary SceneComponent script receivers
through the generated module API library. Deferred inputs, attachment, Tick,
destruction, stale-handle rejection and reload cleanup passed CMD Automation.

Runtime `AddComponentByClass` / `FinishAddComponent` and native constructor
`CreateDefaultSubobject` are distinct entry points. The
[native C# Character example](../tests/Finare.Tests/Fixtures/NativeConstruction/README.md)
now uses `OnConstruct` during the actual UE constructor to create a camera and
mesh, retain a reflected component reference, and preserve edited camera defaults
through save/reopen and independent spawned instances. It uses a temporary
constructor scope without starting a managed World. The constructor program is
checked against the saved class; changed programs require stopping and rebuilding
the CDO. Async loading and cooked delivery remain outside this Editor tranche.

Remaining C-4 work follows concrete public API gaps:

1. Extend the native construction entry for concrete loading/default-edit cases
   beyond the synchronous Character example, including async class loading and
   constructor-default changes across class hierarchies. Keep native constructor
   semantics; do not introduce a component-composition data model.
2. Keep source bindings, saved UE classes, component identity and runtime
   behavior ownership consistent. Preserve explicit default/reference overrides.
3. Exercise save/reopen and permitted stop/edit/restart. Reject active or
   incompatible edits before mutation; failed publication restores the previous
   source, assets, defaults and stopped state.
4. Verify the affected managed contracts and real Editor workflows, including
   existing descendant ownership and publication regressions.

**Done when:** ordinary Actor/component composition works through those public
operations with its remaining ownership cases demonstrated in Editor. Record
cooked acceptance separately if that scope is later resumed. Do not mark the
whole milestone complete from patch generation or an inspector control alone.

Implementation detail and known fixture failures remain in the
[class composition plan](plans/2026-09-09-class-composition.md).
Reuse completed Rename/Duplicate, inheritance and recovery behavior.

### M13.4 — Validate the completed capability level in another real project

- [x] Select the Lyra GAS health/HUD slice and install through the SDK.
- [x] Migrate the production Controller/Pawn and health subscriptions using
  generated APIs while retaining Lyra GAS and the original HUD presentation.
- [x] Record equivalent health/HUD behavior through reload, respawn,
  stop/restart and teardown in two independent Editor copies.
- [ ] Exercise the remaining same-project asset/Soft Reference/async loading
  and failure-cleanup behavior through the installed workflow.

**Done when:** the selected slice and its required asset/async behavior are
usable through the installed product with clear failure handling. The health
sequence already passed within its recorded scope; additional package acceptance
is deferred. See the [pilot plan](plans/2026-09-11-lyra-pilot.md) and
[reproduction guide](../samples/External/Lyra/README.md).

### M13.5 — Broader family follow-ups after the breadth checkpoint

Select further work when an ordinary use case exposes a concrete blocker.
Remaining families include richer reflected/state/container combinations,
ordinary C++ ownership and signatures, callback forms, latent/services,
binary interop and networking combinations. Their current limits are listed
in [capabilities](CAPABILITIES.md).

Do not reopen every family to complete a comparison table. Connect a selected
gap to a public API and a working gameplay outcome, then check affected runtime
and lifecycle behavior. New dependencies must have that concrete consumer.

### M13.6 — Expand delivery and improve measured weak spots

- [ ] Identify and publish a consumer-facing SDK candidate with supported
  versions, upgrade guidance and issue-reporting instructions.
- [ ] Reproduce its external install/upgrade/rollback cycle.
- [ ] Add a consumer-selected engine/platform combination only after its
  build, package and runtime verification.
- [ ] Optimize reproduced scalar, resource/spawn or reload weaknesses and
  rerun affected matched workloads before making comparative claims.

The frozen RC1 record is retained in [release readiness](RELEASE_READINESS.md).
It is not a current-branch release verdict.

## Puerts comparison and capability breadth target

The selected breadth target has been met for the reviewed use cases. Preserve
the typed contract, object ownership, World teardown and rollback guarantees
as new cases are added. The pinned upstream comparison and detailed checkpoint
evidence remain in the [September 11 snapshot](history/ROADMAP_2026-09-11.md#puerts-comparison-and-capability-breadth-target).

The September 15 explicit interop task adds interface arrays/Map values,
interface/collection-bearing USTRUCTs, live native views, one declared ordinary
C++ base chain, retained fixed-signature std::function inputs and synchronous
zero-copy numeric spans over UE/C++ storage. See the
[implementation scope and evidence](plans/2026-09-15-interop-parity.md) and
[example](../tests/Finare.Tests/Fixtures/InteropParity/README.md). This closes
those bounded gaps; it does not establish unrestricted Puerts parity or renew
Cook/platform acceptance.

## Working and verification policy

Finish one meaningful user behavior at a time. Use [TESTING.md](TESTING.md) to
select checks, and [AGENTS.md](../AGENTS.md) for implementation rules. Failed
tests require a diagnosis; a reduced filter does not close their failure.
Broader UE/package/performance suites follow the affected behavior or release
claim. A documentation edit does not invalidate unrelated runtime evidence.

Update this queue when priority, capability or completion changes. Keep commands,
run-specific counts and artifact paths in technical plans or reports. Preserve
historical results with their revision and scope; do not turn them into a second
active backlog. Earlier milestones remain in [ROADMAP_HISTORY.md](ROADMAP_HISTORY.md).
