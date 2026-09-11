# Finare product roadmap

Last reviewed: **2026-09-11**

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
- Current tranche: **M13.3C-4 — class composition in normal authoring**.
- [ ] **M13.3C-4**: finish ordinary Actor/component composition and its
  default/reference ownership through the existing asset workflow.
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
and stopped-hierarchy preservation. These are dependencies of composition;
they do not establish its complete user workflow.

## Delivery order

| Order | User-visible outcome | Completion boundary |
| --- | --- | --- |
| Active: M13.3C-4 | Edit ordinary Actor/component composition through normal authoring while preserving intentional defaults and references | Complete the behavior and Editor checks below |
| Alongside affected changes: M13.4 | Keep the installed Lyra health/HUD workflow usable and close its remaining asset/async coverage | Run relevant public-workflow checks when changed code affects the pilot |
| Later: M13.6 | Ship a clearly identified candidate and extend supported configurations | Explicit release, upgrade and platform acceptance |

External feedback is already part of current development. Do not describe Lyra
as waiting to start, or require every class/API combination before using that
feedback. Conversely, a passing health/HUD slice does not finish all of M13.4.

## Remaining work

### M13.3 — Make Script Classes ordinary Unreal gameplay types

The next deliverable is one complete normal-authoring composition workflow:

1. Edit the relevant Actor/component parent or composition through the existing
   asset/C#/Graph workflow, including the component-family behavior still open.
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
