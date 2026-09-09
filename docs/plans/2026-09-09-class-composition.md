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
native consumers. The existing declaration patch still only updates an asset
path; adding a UI control alone would leave the UE parent unchanged.

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

Then add declaration editing and the native authoring control, using source
preconditions and the existing publication/recovery workflow. Parent changes
must preserve intentional inherited defaults and references, reject cycles and
incompatible ownership, and handle dependent Blueprint classes transactionally.
Parents requiring component composition remain part of C-4; the initial
declaration transport is not completion of the base-class feature or milestone.

## Relevant verification

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
