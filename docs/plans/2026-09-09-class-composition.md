# 2026-09-09 — Class composition in normal authoring

Status: in-progress

Current verification scope is Editor-only at the user's request. Implement and
verify the remaining authoring/runtime behavior in Editor; defer additional
Cook/package checks and do not interpret Editor evidence as final cooked
milestone acceptance.

## Outcome and scope

M13.3C-4 adds explicit base-class changes and component composition to the
normal C#/Graph authoring workflow. Source edits, saved UE classes and runtime
activation must agree about the selected parent. Preserve persistent class and
member identities, deliberate defaults/references, and one managed behavior
owner through compilation, reload and packaging.

The C-3 inherited-class and active Rename/Duplicate workflows are complete in
`0937088`; reuse their identity, archetype and lifetime rules. This work does
not reopen those operations as new capabilities. Existing protected Host files
remain outside the implementation scope.

## Decisions and remaining work

The normal-authoring publication coordinator now reserves an analyzed managed
candidate without swapping its active generation. The native participant joins
prepared source/artifact writes, parent changes, package saves, native history
and a durable completion marker in the class-operation journal. Finish adopts
the reserved managed candidate only after the native participant verifies its
completion marker; cancellation requires restored external-file baselines and
no pending native recovery receipt. Commit/Undo/Redo retain exact transaction,
preview revision, asset and generated-owner identities. An explicit declaration
parent cannot be replaced by a contradictory history override.

Ten focused managed authoring tests pass in
`artifacts/finare-usability/c4-managed-authoring-focused-2.log`. The native
coordinator passes in `c4-coordinator-7.log`, including a saved Actor hierarchy,
external-write rollback, parent history replay, positive-but-mismatched preview
revisions, contradictory declarations and completed-transaction rejection.
`ParentChange.Primitive` and `OperationJournal` pass in their `-2.log` files in
the same directory. These tests do not cover a real World rejecting a no-op
parent publication.

The subsequent Slate recovery edits retain a separate reservation state so
HTTP failure cannot let a new edit overwrite the pending transaction. Cancel
ACKs must identify the exact cancelled transaction and preview; malformed,
foreign or already-adopted replies retain recovery state. These final client
edits now pass `native-cancel-client-build-5.log` and the actual Slate production
response/dispatch/retry test in `c4-native-cancel-client-5.log` (one successful
Automation test, exit 0). An injected transport drives local Apply rejection,
failed HTTP completion, malformed/foreign/adopted ACKs, single-flight retries,
blocked Submit/Refresh, and uncertain prepare recovery. Failed request startup
uses the same handler in production and the test transport. This establishes
the client state paths; it is not a physical toolbar interaction test. Ordinary
Actor composition outside the current carrier family, component-family
proof and affected cooked execution remain unfinished. This is a publication
checkpoint, not C-4 completion.

The native inspector now exposes base-path edit/clear for Actor and
ActorComponent Script Classes through declaration-patch v0.4, keeping static
libraries hidden. It reuses exact class-path validation, including plugin mount
points. Editor build `c4-base-ui-editor-build3-console.log` and the independent
`AuthoringSurface.ClassBasePatch` Automation in `c4-base-patch-1.log` pass.
These cover the patch producer's schema, class kinds, plugin paths, invalid
paths and JSON-null clearing. They do not prove ordinary Actor composition.
The broader Workbench test still assumes a one-asset Demo Surface, while the
checked-in fixture contains 19 assets; its pre-existing fixture failure is
retained in `c4-base-ui-workbench-1.log` and has not been repaired by this slice.

Project authoring now resolves the selected ScriptAsset symbol and the exact
partial file owning its class-binding attribute. Multiple ScriptAsset types in
the selected file reject as ambiguous. Only the primary source and that binding
owner are editable; their physical identity-sidecar paths participate in
publication, external-conflict checks, cancellation, history and project refresh.
The primary source retains its custom sidecar path. A refresh moving the binding
into a new source without a sidecar creates that sidecar and retires the old
artifact baseline, so the next edit can publish normally. Six
`ProjectAuthoringTests` pass, including add/change/clear through native
prepare/finish, cancellation, Undo/Redo, stable IDs and external sidecar conflicts.
This verifies the managed participant, not physical Editor UI interaction.

Runtime generated-function dispatch now selects the unique active component by
the function's declaring generated class, canonical class path and persistent
ClassId. Changing a running component's configured asset leaves calls with its
active generation. Ordinary ABI v1/v2 calls and Blueprint overrides share that
selection; active ambiguity cannot fall through to a cold configured component.
Only initial RepNotify may use a unique configured owner, including
ScriptAssetPath, and its queued target must be that owner's receiver. Enhanced
Input now uses the active generated owner instead of the nearest generated
ancestor. ActorComponent receivers remain the component itself.

Editor build `c4-owner-build4.log` and eight focused Automation tests pass:
Actor/ActorComponent exact-owner dispatch, parent ownership, connected Actor and
component networking, component lifecycle, managed function dispatch with
Enhanced Input, and component parent invocation. Logs use the `c4-owner-` prefix
under `artifacts/finare-usability/`; the last three share
`c4-owner-input-lifecycle.log`. The first build's invalid test helper and the
first Actor run's stale pre-rename declaration path remain in their failed logs.
The networking fixture now reads the installed plugin's current emitted Demo
declaration. This establishes routing and its existing lifecycle/input consumers;
same-parent live publication and SCS composition remain open.

`UnrealScriptClassAttribute.BaseClassPath` now reaches the declaration and
native consumers. The `updateClassBase` declaration patch edits that explicit
attribute argument. A native parent transaction now handles existing carrier
families; joining it to normal source publication remains separate work.

The first contract carries optional `ClassBinding` data through Declaration IR
and the compiled runtime declaration: opaque `Kind`, `BaseClassPath` and
`GeneratedClassPath` strings. Core owns structural validation and serialization;
the C# attribute producer and Unreal adapter own their engine-specific meaning.
CLR inheritance and UE parent selection remain distinct relationships.

Binding-bearing JSON uses `unifiedscript.script-asset/v0.3`; binding-bearing
USRDEC uses version 1.5. Legacy documents and chunks retain their old layout and
remain readable. New semantics must not be accepted under a downgraded version.
The binary asset prefix adds presence/kind/nullable paths before its states.
Null means no explicit path directive; it must not become a guessed parent or
generated class identity. Selecting a default UE parent can use its exact path.

Declaration patches containing `updateClassBase` use
`unifiedscript.declaration-patch/v0.4`; existing operations retain v0.3 when the
new operation is absent. Old schemas reject the new operation. A base edit
requires the current asset identity/path, compilation/configuration IDs and
complete class binding as preconditions. Roslyn resolves the type's own
attribute syntax across partial files; only `BaseClassPath` changes. CLR base
types, generated owner path, class/member IDs and source ownership stay intact.
Removing the argument clears the explicit directive and keeps the current UE
parent; it does not request a default parent. Candidate source is reanalyzed
before publication, and the existing Authoring workspace owns commit/undo/redo.

Native JSON/binary materialization now carries the selected binding into the
state migration report. The component's existing startup/reload preflight and
Editor materializer validate the actual generated owner's identity, class
family and explicit parent. Path comparison is case-sensitive, including the
loaded owner's canonical path; ordinary Blueprint descendants remain valid
targets. Runtime rejects a parent mismatch before activation. The Editor now
handles an explicit parent change separately from current-owner validation.
Combined parent/member planning uses the original default baseline and the
prospective parent's member constraints.

The Editor parent operation keeps runtime class-binding validation strict.
It validates the current generated owner independently of the requested parent,
then checks the exact prospective class, its family and ancestry before planning
members against it. A parent shape change must reject affected active behaviors
before compilation or original-object mutation. Initial native execution keeps
the existing behavior carrier; leaving that carrier family must report that
composition is required before mutation, and remains unfinished C-4 work.

Plan descendant conflicts against the original hierarchy, since a disposable
owner clone has none of its existing descendants. Compile the prospective parent
and member changes together: an override removed by the same declaration update
must not make an intermediate, old-member candidate fail before that update is
applied. Existing user graphs and descendant variables remain protected.

Use UE's object transaction serialization for the in-memory parent/graph/SCS
rollback without modifying the user's undo history, together with the existing
class-operation journal for saved package recovery. A disposable candidate must
compile during validate-only. Restoring only `ParentClass` or `NewVariables`
after failure is insufficient; descendant defaults/references and the generated
behavior bindings participate in the same operation.

Graph-pin transaction restoration queries `GEditor->Trans`, so directly applying
a detached `FTransaction` is insufficient. Use an isolated `UTransBuffer` for
the synchronous operation and its rollback, restoring the previous transactor
and active `GUndo` on every exit. The operation must not clear or append to the
user's undo history. Strongly retain the previous transactor while it is removed
from `GEditor->Trans`; the saved raw `GUndo` belongs to that host. Stack-owned
component snapshots must also retain their UObject references across compiler GC.
The private buffer preserves normal UE custom-change handling. It records
Blueprint/graph/SCS authoring state, not serialized GeneratedClass or skeleton
class layouts. Rollback must rebuild the restored authoring in parent-before-child
order and restore the captured managed binding data; reassigning only the parent
does not restore the compiled behavior. Keep compiler work inside the isolated
transactor until both authoring and generated state have been restored.
Keep value snapshots and replacement tracking alive through
package publication; a failed rollback must retain its recovery receipt.

Reuse the journal's atomic snapshot restoration for live rollback instead of
capturing and writing package bytes a second time. A receipt may become terminal
only after successful rollback or after native state and package publication are
committed. Failed receipt writes must preserve the prior in-memory phase as well
as the pending disk receipt; cleanup errors must not be cleared by a later
successful transaction-finalization call.

The persistent two-parent Actor fixture now checks scalar, collection and
external-reference follow/override values across owner and descendant CDOs,
stopped instances and actual package reload. The primitive also covers inherited
string/bitfield values, owner-local state rename rollback and component defaults
on an Actor CDO hierarchy. Typed property-bag snapshots supplement UE reinstancing
and compare exact reflected types before original mutation. Owned components
remain attached to their current owner/archetype; editable values are restored
without importing an old CDO's subobject pointer into a different owner.

Capture UObject identity before compilation using its object index and serial,
not a raw address that can be reused during later recompilation. Resolve a CDO
through its exact authoring Blueprint and current generated class; validate the
class owner when resolving an instance through replacement notifications. The
same identity rule applies to collection-reference remapping. An unresolved
replacement is not permission to clear a preserved reference or select another
object with a matching name. Retain replacement destinations strongly until
the operation ends: a worldless replacement otherwise has no World to keep it
alive through compiler GC. Do not create permanent roots. Component defaults
follow their immediate archetype unless explicitly overridden. Check exact
component Outer and archetype objects, not only their names or classes. The
root behavior's ScriptAsset binding is preserved even when it equals the old
ordinary parent's value; descendants retain their own follow/override policy.
The stopped-instance test must keep the script stopped through reinstancing,
then explicitly restart it. `UFinareComponent::BeginPlay` can start an
automatically configured behavior, so rejecting already active instances does
not by itself establish that compilation cannot activate a replacement.
Any activation suppression must cover only the affected authoring owner and its
derived classes, remain in effect through rollback recompilation, and release
on every exit. Changing `bStartAutomatically` to avoid activation would alter
user configuration and the default-inheritance baseline. UE's actor reinstancer
can dispatch BeginPlay again for an already-begun Actor; successful native
reinstancing alone is not evidence that the behavior remained stopped.
Mixed parent/member edits now retain rollback values for owner-local managed
properties: transaction deserialization cannot recover a value whose old field
is absent until authoring has been recompiled. Restore those typed values by
persistent state identity after rebuilding the old layout, while successful
declaration-default changes continue to follow the existing state plan.

Use the current selected generated owner for path validation, rather than an
ordinary descendant instance's concrete class. Do not freeze mutable generated
paths into the managed activation contract captured at first creation: C-3
already permits compatible Rename with a stable ClassId. The factory's
RequiredTargetClass and native behavior component also need reconciliation
when a parent change leaves the original Finare native root family.

Next connect the native authoring control using the source patch preconditions
and the existing publication/recovery workflow. Parent changes
must preserve intentional inherited defaults and references, reject cycles and
incompatible ownership, and handle dependent Blueprint classes transactionally.
Parents requiring component composition remain part of C-4; the initial
declaration transport is not completion of the base-class feature or milestone.

The current Workbench transaction publishes managed source/artifacts/history
before Slate queues the separate daily Build & Reload. Its successful response
is therefore not a transaction across source and UE parent changes. The parent
operation must cover both publication and UE mutation through the existing
class-operation recovery journal; a later failed daily build cannot be presented
as an atomic rollback. The native parent transaction now restores authoring,
compiled parent/bindings and component ownership on failure. Deliberate
descendant/instance defaults and mixed parent/member rollback values still need
typed snapshots and their focused tests.
The standalone CLI `apply-declaration-patch` entrypoint still constructs a
single-source request. Native integration must retain the project sources,
references and identity sidecars already used by the Workbench and C-3 class
operations; it cannot reduce a partial class to that single-file entrypoint.
`ProjectAuthoringInput.Load` already retains evaluated project sources,
references and identity sidecars, but marks every non-primary source read-only.
The parent control must resolve the class attribute's actual partial owner and
respect that source's editability. Loading the full project alone does not make
an attribute in a different partial file writable. The existing Workbench preview
retains a candidate snapshot behind its preview token; it currently exposes
source/graph differences, not a prepared native-parent commit boundary.
`CoreClrDebugHttpServer.CreatePreview` retains the analyzed candidate and
`TakePreparedPreview` consumes it when committing. Slate's `HandlePatchResponse`
adopts the already-published workspace before queuing the daily build. Parent
publication must join that transaction boundary rather than treating a later
build failure as a successful source edit. Do not leave the private
`GEditor->Trans`/`GUndo` swap installed while awaiting an asynchronous Workbench
HTTP response; the current native isolation scope is synchronous.

The native parent preview must use the analyzed candidate declaration bound to
the Workbench preview token. The current authoring-preview v0.3 response exposes
source/graph-layout differences, but not that candidate declaration; reading the
existing declaration file would instead validate the old published program.
Keep the selected asset identity, baseline revision and transaction identity
bound to the candidate across native preflight and publication. The ordinary
`HandlePreviewResponse` path currently dispatches the source commit immediately
after diff confirmation, so parent edits need an explicit native participant
before reusing that dispatch path. No parent control exists in the native
authoring surface yet.

Actor composition must reconcile the factory's `RequiredTargetClass` and its
native `Finare` component when leaving `AFinareActor`. Ordinary
managed function dispatch currently finds the first Script Component on an
Actor, while Blueprint overrides already select an exact active owner. Multiple
composed behaviors need exact function-owner routing before a second carrier is
introduced; preserve one owner per behavior rather than relying on component
enumeration order.

## Native publication integration decision — 2026-09-11

Checkpoint: `f3ebe07` adds prepared debug reloads and the managed suite passes
1093 tests. `bd3fa0b` adds external publication to the native Blueprint journal;
Editor build, the extended `ParentChange.Primitive` test and `OperationJournal`
pass. The new case fails after both an existing and a newly created output have
been written, and restores package bytes, source bytes, graph/state identities,
three distinct CDO values and component ownership. Logs are under
`artifacts/finare-usability/c4-parentchange-external-3-engine.log` and
`c4-operationjournal-external-engine.log`. Normal Workbench coordination,
partial-file ownership, durable completion and composition remain unfinished;
this checkpoint does not close C-4.

The next delivery joins the existing Workbench prepared candidate to the native
class-operation journal. It does not commit a source edit first and attempt to
compensate later with ordinary Undo or a separate daily build.

The confirmed preview identifies every changed native class binding and carries
the analyzed candidate declaration, stable ClassId, generated-owner path and
baseline binding. A native-publication prepare step reserves that exact
transaction and prepares the complete source, identity, layout, declaration,
surface, PE/PDB, chunk and history outputs without writing their destinations.
It retains the existing project evaluation, references and source ownership;
an attribute in another partial file must be resolved to its actual editable
owner rather than changing the primary file or making every project file writable.
Concurrent authoring mutations are rejected while publication is reserved.
Preparation also loads the candidate debug program without replacing the
currently published generation. Cancellation disposes that candidate. Completion
adopts the already prepared program and history; it must not recompile, rewrite
the published files or roll source back after the native participant committed.
Duplicate completion requests are resolved by their exact transaction/token
identity before ordinary revision or already-used-transaction rejection.

The native participant validates the selected Script Asset/Blueprint and all
prepared file baselines before mutation. Its synchronous materializer transaction
captures the external files in the same recovery journal as the UE packages,
applies the candidate parent and declarations, saves the UE state and publishes
the prepared file bytes. Any failure restores both sides, including previously
absent outputs, intentional values/references and the original package dirty
flags. No HTTP request or asynchronous wait may retain the private
`GEditor->Trans`/`GUndo` swap. Existing parent rollback remains the implementation
for native recovery; a second ad hoc Blueprint snapshot scheme is unnecessary.

After native success, Workbench completion adopts the reserved candidate and
history. Completion is idempotent by transaction identity; a lost response is an
uncertain acknowledgment, not permission to republish or report rollback. A
durable native completion/recovery result must distinguish a committed operation
from an interrupted receipt. The already published history also allows a new
Workbench to reopen the committed source. A native failure cancels the reserved
candidate without introducing an authoring Undo/Redo entry. Recovery failures
retain their receipt and block conflicting publication until resolved.

Normal declaration edits, source edits and history navigation must detect native
binding changes consistently. Slate routes such previews through the native
participant and exposes a base-class control. A client without that participant
must receive an actionable rejection before publishing incompatible source;
it must not bypass the joint transaction through another HTTP route.

History also needs the native parent actually observed before and after each
transaction. An omitted `BaseClassPath` means "keep the current parent", so
restoring that source text alone cannot undo A-to-B reparenting. Persist this
native history by authoring transaction ID in an editor-owned sidecar included
in the same journal/publication. Undo/Redo uses the recorded actual parents,
preserves the original source spelling, and validates its selected owner and
baseline. Do not guess A from a default class or introduce UE types into Core.

Actor composition follows this publication batch. Moving outside the native
Finare Actor carrier must create/reconcile the owned script component and
RequiredTargetClass, preserve user components and inherited overrides, and route
managed functions by the exact generated owner. Component enumeration order is
not ownership. Component-family changes and two composed behaviors require their
own runtime and package proof before C-4 is marked complete.

For an ordinary Actor parent, the owned carrier must use UE's construction-script
component template and inheritance mechanism; it cannot assume every carrier is
a native subobject directly under the Actor CDO. Match the selected ScriptAsset
and generated owner, and preserve unrelated user components. Ordinary function
dispatch needs the same exact-owner selection as overrides. Initial RepNotify
queuing occurs before activation, so configured ownership must remain available
without requiring an already active managed generation.

Use the active ScriptAsset while a component is running; changing its configured
asset must not redirect calls away from the still-active generation. Before
activation, resolve only the configured ScriptAsset/ScriptAssetPath and match
the exact generated owner and stable ClassId for initial RepNotify queuing.
Ordinary function ABI v1/v2, overrides and automatic input/delegate binding
must agree on that ownership. Multiple matches are an error, not a first-match
fallback. A same-parent native publication can still reinstance components;
its real-World verification must show that stopped behaviors stay stopped
through publication and rollback before an explicit restart.

`FinareClassFactory` writes `requiredTargetClass` into the deployment manifest
as well as the ScriptAsset. Composition must update that manifest entry in the
joint publication when changing the requirement; changing only the asset would
be undone by later synchronization. Keep ActorComponent-family reparent proof
distinct from supporting arbitrary native ActorComponent base classes, which do
not automatically acquire the existing managed host lifecycle.

## Relevant verification

### Finare-name delivery checkpoint — 2026-09-11

The committed `a3a1ab2` source built and cooked as FinareHost for Development and
Shipping in the independent `D:/Project/FinareUsabilityGate-20260911` checkout.
Both packaged CoreCLR/ABI checks passed using their own staged chunk and runtime
with poisoned external DotNet roots. Evidence:
`artifacts/finare-usability/release-a3a1ab2/release-gate.json`.

The three existing local Demo assets were migrated in that checkout using an
exact, temporary native-name redirect configuration. After removing it, a fresh
process loaded the assets and synchronized the existing Script Class. The class
ID and five authored graph GUIDs were unchanged; exported CDO and scene text
matched their original values after the intended widget-class string rename.
The verified assets were published back only after checking the original file
digests. Original bytes remain under
`artifacts/finare-usability/demo-rename-original`; no persistent redirects were
added to the project.

The Async UI scene was then cooked explicitly in a separate Development demo
delivery. Its packaged test passed saved-scene loading, viewport ownership,
cancel, asynchronous mesh spawning, chunk reload and teardown. Evidence:
`artifacts/finare-usability/demo-delivery-runtime.log`; launcher:
`artifacts/finare-usability/demo-delivery-a3a1ab2/Windows/StartAsyncDemo.cmd`.
The general release package above does not include this scene. These results
cover the renamed existing Demo, not the unfinished C-4 publication/composition
code or the Lyra gameplay pilot.

### Parent transaction checkpoint

The native parent transaction checkpoint passed the Editor build and six CMD
tests under `Finare.Unreal.ScriptClass`: `ParentChange.Primitive`,
`BlueprintStateMaterialization`, `BlueprintInheritedEventMaterialization`,
`CollectionStateMaterialization`, `InheritedAddedStateDefaults` and
`InheritedCollectionDefaults`. Evidence is in
`artifacts/m13-3c-4-class-composition/parent-change/editor-parent-change-build-17.log`
and `editor-parent-change-values-7.log`, with six-test queue completion.
The final root ScriptAsset binding protection passed a subsequent Editor build
and the affected `ParentChange.Primitive` test in `editor-parent-change-build-18.log`
and `editor-parent-change-values-8.log` in the same directory. The other five
tests were not rerun for that parent-only binding change.

The primitive uses two ordinary Blueprint parents and an Actor owner/child/
grandchild CDO hierarchy. It checks inherited string and native bitfield
followers/overrides, component editable defaults, exact component Outer and
immediate archetype, and exact ScriptAsset binding when the new parent has none.
A mixed parent/state rename failure restores distinct owner/child/grandchild
values (17/23/31), graph/state GUIDs, superclass, package bytes and dirty state.
An existing caller transaction retains its identity and record count across
preflight, success and failure. The collection regression separately verifies
reinstanced UObject Map keys and covers the worldless replacement that required
operation-scoped destination roots.

The real `ParentChange.ActiveWorlds` Actor fixture saves the owner, ordinary
parent and child/grandchild packages and runs managed chunks in two Game Worlds.
Both validate-only and apply reject active instances without changing the
parent, CDO, package bytes or runtime identities; stopping only one World still
rejects the edit. Once both stop, reparenting preserves scalar/array/set/map
values, exact component ownership and ScriptAsset binding. Reinstanced actors
remain stopped until explicitly restarted in their respective Worlds.
The first run, `editor-parent-change-active-worlds-1.log`, reproduced unwanted
automatic activation from UE's reinstanced `BeginPlay`. An editor-only scope
now suppresses activation for the exact authoring owner and its descendants,
through both commit and rollback, without changing automatic-start defaults.
Editor build 21 and `editor-parent-change-active-worlds-2.log` passed
`ParentChange.ActiveWorlds` and `ParentChange.Primitive` with this fix.
The same invocation's old inherited-state fixture failed to load
`Finare.Unreal`; its published dependencies differ from the current
fixture. The unchanged `InheritedActorStateRuntime` test passed separately using
the current `runtime-fixture/BlueprintInheritedStateFixture.config.json`, in
`editor-parent-change-inherited-regression-1.log`. The old fixture failure is
retained rather than reported as a successful three-test run.

The extended real-World after-save failure exposed two additional recovery
requirements. Snapshot replacement tracking must receive the final
`OnObjectsReinstanced` map as well as early `OnObjectsReplaced` notifications;
self-pairs are observations, not replacement edges. More critically, the
private Undo must record only explicitly captured authoring objects. UE's
reinstancer calls `Modify` and `EditorDestroyActor` on runtime objects; recording
their World/Level/Actor changes alongside the authoring snapshot corrupted
live-instance recovery. The transaction now applies its captured-object scope
to object, array, snapshot and custom-change recording, retaining standard flip
Undo for the captured authoring tree and the UStruct layout exclusion.
Explicit-null replacements still fail recovery rather than silently clearing
preserved values. Failed receipts from these investigations are retained as
`active-worlds-3-pending-receipt.json` and
`active-worlds-4-pending-receipt.json` in the parent-change artifact directory.

Editor build 25 and `editor-parent-change-active-worlds-5.log` passed
`ParentChange.ActiveWorlds`, `ParentChange.Primitive` and
`CollectionStateMaterialization`, with three-test queue completion. The extended
World test checks nested activation scopes, an unaffected ordinary parent,
side-effect-free rejected starts, after-save rollback with exact package bytes,
complete instance values, World/component/archetype/ScriptAsset identity, and
explicit restart after both rollback and success. Managed signature markers
and native actor rotation verify actual collection consumption after restart;
script, lifecycle-claim and World counts return to baseline on teardown.

Only the managed owner package is saved by the primitive test; its ordinary
parents and descendants are transient. The ActiveWorlds fixture saves the
hierarchy but does not unload/reload it after reparenting. The new
`ParentChange.PersistentValues` fixture closes that specific Actor persistence
gap with two saved ordinary parents and three independent external asset
packages. Owner, child and grandchild have literal expected follower/override
combinations for an integer, an object reference, object array/set and
object-keyed integer map. Set/Map checks use the actual hash lookup APIs with
property-initialized key storage. Stopped live instances preserve their distinct
managed values and current inherited values, component Outer/archetype and
ScriptAsset. A candidate changing only `ParentAsset` from object to string
rejects in both validate-only and apply, naming the explicit reference override;
the current CDO identities, values and saved hierarchy bytes remain unchanged.

After saving the hierarchy, ScriptAsset and external assets, the fixture destroys
the original Worlds, releases replacement roots, unloads the packages and runs
GC. Weak references prove the old parents, owner, descendants, ScriptAsset and
external assets are gone. Exact-path reload reconstructs the B-parent hierarchy
and resolves every reference to the reloaded asset. Two new Worlds execute the
persisted managed scalar/collection values, checked by complete state, signature
markers and native actor yaw; script, lifecycle-claim and World counts return to
baseline. These are saved class/asset packages, not saved World maps or savegames.

Editor build 28 and `editor-parent-change-persistent-values-2.log` passed this
test with one-test queue completion. The first CMD run is retained: its abstract
`UDataAsset` instances could not be saved. The fixture now constructs concrete
Engine `UMeshDeformerCollection` assets while retaining `UDataAsset` property
types. This checkpoint adds tests only; production preservation code has not
changed since the preceding ActiveWorlds verification.

Combined parent/declaration-default edits, reference preservation through mixed
edit rollback, normal-authoring publication and cooked acceptance remain
unfinished. Component-family parent changes are not established by the
Actor-only parent tests. This checkpoint does not close C-4.

Shared journal changes are committed locally as `4e3b45e`. The earlier
`editor-parent-change-transactions-1.log` records successful `OperationJournal`
and `AssetOperations` tests, including actual write/restore failures, unchanged
failed-write phases, targeted recovery/retry and a serialized `parentChange`
receipt. Those journal files have not changed since that verification.

The source-patch checkpoint passed 50 focused managed regressions, including
nine `ScriptClassBasePatchTests` cases plus the existing declaration patch JSON,
C# declaration applier and Authoring workspace suites. These cover cross-file
partial ownership, add/change/remove, constant-expression and separator comments,
stable member IDs, mixed state/base edits, old-schema rejection, v0.4 old-only
normalization, conflicting asset targets and workspace commit/undo/redo.
The final log/TRX are
`artifacts/m13-3c-4-class-composition/base-patch/base-patch-regression-final-2.*`.
Builds used `FinareSkipManagedHostStaging=true`. This managed-only edit
did not require a new native build, deployment or CMD run; the prior native
evidence below remains evidence for declaration transport and existing behavior.

The managed declaration transport passed 98 focused lowering, JSON and
compiled-chunk checks. Three additional selected cases cover v0.1/v0.2 downgrade
rejection and mixed binding/nonbinding assets in USRDEC 1.5. Logs and the actual
emitted fixture are under `artifacts/m13-3c-4-class-composition/class-binding/`.
The fixture is emitted by `ScriptClassBindingTests.EmitsClassBindingJsonAndChunkFixtureForNativeConsumer`.
Use `-p:FinareSkipManagedHostStaging=true` for focused managed builds;
the Demo staging target now also respects this flag.

Editor build passed (`editor-class-binding-build-3.log`). Seven selected CMD
Automation tests passed with queue completion (`editor-class-binding-automation-1.log`):
class-binding parser, actual JSON/chunk reader, owner/ordinary-child consumer,
legacy declaration/collection materialization and existing Script Asset tests.
The logs retain pre-existing Engine startup `Condition failed` messages, also
present in C-3 fixture 6; the selected tests report Success.

The existing `emit-unreal-demo-chunk.ps1 -Configuration Release` deployed the
matching host, Demo and chunk successfully (`managed-deploy-class-binding.log`).
`Finare.Unreal.ScriptClass.CommonStateMaterialization` then passed with
queue completion (`editor-class-binding-runtime-1.log`), covering the existing
gameplay state/reload path with the new declaration format. These four logs are
under `artifacts/m13-3c-4-class-composition/`. No additional native rebuild was
needed for the final documentation changes.

The declaration-transport checkpoint alone does not establish a native parent change, a live
World continuing after a binding-mismatch rejection, or C-4 cooked acceptance.

Use focused managed tests for actual attribute lowering, persistent IDs, strict
JSON/binary compatibility, and transactional source edits. Native tests must
consume those emitted declarations, verify saved parent and component archetypes,
preserve deliberate overrides/references, and retain running Worlds after
rejection. Complete the connected authoring and affected cooked path before
closing C-4. Producer-only tests do not establish parent changes in UE.
