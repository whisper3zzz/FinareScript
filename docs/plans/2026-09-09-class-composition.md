# 2026-09-09 — Class composition in normal authoring

Status: in-progress

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

Complete parent-value acceptance still needs a persistent two-parent hierarchy
with scalar, collection and external-reference overrides on the owner,
descendants and stopped instances. The primitive currently covers inherited
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
then explicitly restart it. `UUnifiedScriptComponent::BeginPlay` can start an
automatically configured behavior, so rejecting already active instances does
not by itself establish that compilation cannot activate a replacement.
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
when a parent change leaves the original UnifiedScript native root family.

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

Actor composition must reconcile the factory's `RequiredTargetClass` and its
native `UnifiedScript` component when leaving `AUnifiedScriptActor`. Ordinary
managed function dispatch currently finds the first Script Component on an
Actor, while Blueprint overrides already select an exact active owner. Multiple
composed behaviors need exact function-owner routing before a second carrier is
introduced; preserve one owner per behavior rather than relying on component
enumeration order.

## Relevant verification

The native parent transaction checkpoint passed the Editor build and six CMD
tests under `UnifiedScript.Unreal.ScriptClass`: `ParentChange.Primitive`,
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

Only the managed owner package is saved by this primitive test. Ordinary parents
and descendants are transient; persistent hierarchy unload/reload, complete
parent-change collection/external-reference cases, real active-World rejection,
stopped-instance restart, normal-authoring publication and cooked acceptance
remain unfinished. Component-family parent changes are not established by the
Actor-only parent test. This checkpoint does not close C-4.

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
Builds used `UnifiedScriptSkipManagedHostStaging=true`. This managed-only edit
did not require a new native build, deployment or CMD run; the prior native
evidence below remains evidence for declaration transport and existing behavior.

The managed declaration transport passed 98 focused lowering, JSON and
compiled-chunk checks. Three additional selected cases cover v0.1/v0.2 downgrade
rejection and mixed binding/nonbinding assets in USRDEC 1.5. Logs and the actual
emitted fixture are under `artifacts/m13-3c-4-class-composition/class-binding/`.
The fixture is emitted by `ScriptClassBindingTests.EmitsClassBindingJsonAndChunkFixtureForNativeConsumer`.
Use `-p:UnifiedScriptSkipManagedHostStaging=true` for focused managed builds;
the Demo staging target now also respects this flag.

Editor build passed (`editor-class-binding-build-3.log`). Seven selected CMD
Automation tests passed with queue completion (`editor-class-binding-automation-1.log`):
class-binding parser, actual JSON/chunk reader, owner/ordinary-child consumer,
legacy declaration/collection materialization and existing Script Asset tests.
The logs retain pre-existing Engine startup `Condition failed` messages, also
present in C-3 fixture 6; the selected tests report Success.

The existing `emit-unreal-demo-chunk.ps1 -Configuration Release` deployed the
matching host, Demo and chunk successfully (`managed-deploy-class-binding.log`).
`UnifiedScript.Unreal.ScriptClass.CommonStateMaterialization` then passed with
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
