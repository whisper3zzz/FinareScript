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
attribute argument, while changing the saved UE parent remains separate work.

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
targets. The current Editor consumer rejects a parent mismatch before mutation.
Next, plan the parent change against the prospective parent, rather than
validating new members against the previous one and mutating later.

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
as an atomic rollback. The native materializer currently restores only its
variable declarations on apply failure, so reparent rollback also needs the old
parent, graphs, component templates and deliberate descendant/instance defaults.
The standalone CLI `apply-declaration-patch` entrypoint still constructs a
single-source request. Native integration must retain the project sources,
references and identity sidecars already used by the Workbench and C-3 class
operations; it cannot reduce a partial class to that single-file entrypoint.

Actor composition must reconcile the factory's `RequiredTargetClass` and its
native `UnifiedScript` component when leaving `AUnifiedScriptActor`. Ordinary
managed function dispatch currently finds the first Script Component on an
Actor, while Blueprint overrides already select an exact active owner. Multiple
composed behaviors need exact function-owner routing before a second carrier is
introduced; preserve one owner per behavior rather than relying on component
enumeration order.

## Relevant verification

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

This checkpoint does not establish a successful native parent change, a live
World continuing after a binding-mismatch rejection, or C-4 cooked acceptance.

Use focused managed tests for actual attribute lowering, persistent IDs, strict
JSON/binary compatibility, and transactional source edits. Native tests must
consume those emitted declarations, verify saved parent and component archetypes,
preserve deliberate overrides/references, and retain running Worlds after
rejection. Complete the connected authoring and affected cooked path before
closing C-4. Producer-only tests do not establish parent changes in UE.
