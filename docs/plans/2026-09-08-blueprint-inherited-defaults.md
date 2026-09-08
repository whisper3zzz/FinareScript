# 2026-09-08 — Blueprint inherited defaults and component identity

Status: in-progress

## Outcome and scope

Begin M13.3C-3 with ordinary Blueprint children of a managed Script Class.
The configured Script Asset on an Actor's native ScriptComponent must have a
defined inheritance path, and intentional class/instance defaults must survive
base-class synchronization and Blueprint recompilation. Preserve the exact
managed owner, state semantic IDs and World lifetimes.

This first batch uses the existing UE parent chain. General managed-to-managed
parent bodies and authoring changes to the base class remain separate C-3/C-4
work. Do not use a guessed Script Asset or the first available component to
repair missing ownership.

## Decisions and remaining work

The C-2 DLL reload fixture exposed a concrete investigation target: assigning
ScriptAsset directly to the managed Actor CDO's native ScriptComponent did not
populate the ordinary Blueprint child's corresponding component. Reacquiring
the CDO after synchronization and recompiling did not resolve it. The retained
logs are `artifacts/m13-3c-2-event-reload/blueprint-event-reload-child-fix.log`
and `blueprint-event-reload-child-fix-2.log` in the same directory. This is
evidence about direct fixture CDO writes, not yet proof of a defect in the
normal authoring workflow. The C-2 reload test instead configures its real
instances before native BeginPlay; it makes no inherited-subobject claim.

The real-factory regression now confirms the supported native path: the base,
ordinary child, grandchild and spawned child instance retain the exact Script
Asset, stable `UnifiedScript` component name and immediate component archetype.
Editing both automatic-start/tick bools through reflected property notifications
survives base compilation and inheritance. Saving and unloading all four assets
invalidates their old weak references; reloading preserves the same defaults and
identity relationships. No production ownership fallback was needed. This does
not exercise managed startup or establish the exact cause of the old transient
fixture failure.

`CaptureCollectionInstanceOverrides` in `UnifiedScriptBlueprintClassMaterializer.cpp`
now includes current generated descendant CDOs and live instances, excluding
skeleton/obsolete classes and other templates. It records whether each value
follows its immediate archetype or owns a whole-value override. Both validation
and apply reject incompatible shapes before compiling the original Blueprint.
Restoration preserves stable IDs and the existing object-reference fixup/rehash
path. Derived packages whose CDO values were repaired are marked dirty only
after restoration succeeds; dependent assets are not automatically saved.

The inheritance rule is per immediate archetype, not a comparison of every
descendant against the base class CDO. A child CDO may own an override while
its grandchild and instances follow it. Recompilation must restore deliberate
whole-value overrides before propagating the final defaults to their followers;
restoring only the child CDO afterward would leave already-reinstanced followers
with Unreal's intermediate entry-by-entry collection delta. Preserve this
relationship through the existing replacement map and preflight all target
properties before writing. Reject incompatible types when any affected class
default or instance owns an override; untouched values may follow the new type.

The new-field regression creates ordinary child/sibling/grandchild Blueprints
and live instances in two Worlds before adding int/Array/Set/Map fields. The
base initially has a sentinel field whose existing defaults and overrides must
survive. After reacquiring the replacement Actors, the original default
propagation path passes; no new-field follower restoration was necessary.
Early failures read old Actor pointers or assumed a direct CDO write had already
updated its existing followers. Those fixture failures do not establish a
new-field propagation defect.

The regression then establishes child whole-value Array/Set/Map overrides and
their immediate followers before updating the base defaults. Saving and
unloading all four public Blueprint packages invalidates their old asset and
Script Asset weak references. Reloading preserves the Script Asset identity,
new scalar default, complete collection overrides and values on newly spawned
descendants. The existing collection restoration path supplies this behavior.

The production change rejects a newly declared state whose name collides with
a loaded descendant's own user Blueprint variable, before either validation
or apply can compile the original Blueprint. The rejection preserves generated
class identities, hierarchy, variable GUID/name/value and package dirty flags.
The native regression alone does not establish managed startup/reload or cooked
execution; the separate runtime checkpoint below now covers the former.

The factory uses public saved assets and calls `MarkBlueprintAsModified`, which
refreshes Unreal's cached post-construction property list. The regression uses
that authoring path instead of assigning ownership separately to descendants.

Native factory checkpoint: `UnifiedScript.Unreal.ScriptClass.InheritedActorComponentDefaults`
passes after a HostEditor build. Evidence is under
`artifacts/m13-3c-3-inherited-defaults`: `editor-build-inherited-defaults-cleanup-final.log`
and `inherited-defaults-cmd-cleanup-final.log` (Success and Queue Empty). The
test unloads only its four GUID-named packages before removing their files;
the final run cleans them without manual intervention.

Existing-collection checkpoint: `InheritedCollectionDefaults` covers complete
Array/Set/Map overrides, a following grandchild, an untouched sibling, instances
in two Worlds, new instances, stable-ID rename, rejected incompatible retype and
accepted pure-follower retype. Rejections preserve generated-class identities,
values and package dirty flags. The final native checks pass with Success and
Queue Empty under `artifacts/m13-3c-3-inherited-collections`:

- `editor-build-final-5.log` compiles the final materializer; `editor-build-final-8.log`
  compiles the final test and links the Editor.
- `inherited-collections-cmd-final-5.log` — `InheritedCollectionDefaults`.
- `collection-state-cmd-final.log` — existing `CollectionStateMaterialization`,
  including object-reference Map replacement/rehash.
- `blueprint-state-cmd-final.log` — `BlueprintStateMaterialization`.

New-field and persistence checkpoint: `InheritedAddedStateDefaults` passes with
the final materializer and fixture in `artifacts/m13-3c-3-added-state`:

- `editor-build-final-guard.log` — compiles both changed native files and links
  the HostEditor.
- `added-state-cmd-final-guard.log` — Success and Queue Empty, including package
  unload/reload, complete collection values and collision rejection.
- `InheritedCollectionDefaults-cmd-final.log`,
  `CollectionStateMaterialization-cmd-final.log`, and
  `BlueprintStateMaterialization-cmd-final.log` — related native regressions
  pass. Subsequent edits only remove unreachable new-field rollback code and
  strengthen the new fixture's identity/unload checks.

With the guard disabled, `added-state-cmd-no-conflict-guard.log` records the UE
compiler renaming the child's `UserConflict` to `UserConflict_0`; both rejection
assertions and variable-preservation assertions fail. That run also exposed a
separate fixture lifetime issue: the saved Script Asset's standalone reference
survived the first unload. The final fixture clears that lifetime pin after
saving, collects garbage after unload, and asserts all old weak references are
invalid before reading the saved packages. The no-guard run is collision-defect
evidence, not passing persistence evidence.

An earlier `CommonStateMaterialization` run failed during managed activation:
`CreateScriptWithBlueprintContract` cannot load `UnifiedScript.Unreal, Version=1.0.0.0`.
See `common-state-cmd-final.log`. A read-only decode of the staged Demo chunk's
DependencyManifest confirms that all six shared managed dependency SHA256 values
differ from the DLLs in `Binaries/Managed`. The Unreal DLL exists; its digest is
`e4ac8a41292b1c81b01a90acdbf6f99bcf1d263bf27eb64193e426e48c25c6c4`, while the
chunk requires `1325cd60de53b2e814fb1962de86f2dad472e5e65c9069b3f83bed62600b992e`.
The chunk load context verifies shared assemblies through
`UnifiedScriptManagedDependencyCache.Verify`, which rejects differing digests.
At the recovery point, all six staged DLLs matched the Release host output. Running
`tools/emit-unreal-demo-chunk.ps1 -EngineRoot D:/UE_5.8 -Configuration Release -SkipManagedBuild`
restores the matching chunk and declaration without rebuilding or restaging the
host. `CommonStateMaterialization` now passes with actual managed startup/reload;
see `artifacts/m13-3c-3-inherited-runtime/emit-demo-release.log` and
`common-state-cmd-release-chunk.log`. This confirms the deployment-identity cause
of the earlier failure. It does not itself add inherited runtime coverage.

For inherited runtime and cook coverage, use a separate managed test fixture
with scalar and complete Array/Set/Map state. A generated C# constant binds its
`GeneratedClassPath` attribute to the same GUID directory used by the native
Blueprint fixture. Build and emit must consume that exact constant, keeping
the compiled state-member path and the native class identity consistent. This
avoids borrowing or overwriting fixed user Demo packages. Configure ownership
only on the managed base's native ScriptComponent through the factory's normal
CDO/Blueprint notification path; ordinary descendants and instances must inherit
it. BeginPlay, native side effects, state flush, per-World reload and teardown
must then run from the saved and reloaded Blueprint hierarchy.

Compile this isolated fixture against the deployed managed host DLLs. A source
`ProjectReference` rebuild changed all six dependency digests after the local
commit: the staged Unreal DLL records informational version `1.0.0+16f51e2...`,
while the rebuilt Release DLL records `1.0.0+2ce634b...`. The resulting fixture
chunk correctly requires the rebuilt files and is rejected by the unchanged
staged host (`inherited-state-cmd-2.log`). Referencing the deployed binaries
keeps this fixture's compiler and runtime on the same dependency identities,
without restaging the host, altering informational versions or weakening digest
verification. Generated bindings remain the normal native call surface.

Inherited Actor runtime checkpoint: `InheritedActorStateRuntime` passes with
the isolated managed fixture built against those staged binaries. It saves,
unloads and reloads three public Blueprint packages plus a separate Script Asset
package before spawning the ordinary child and grandchild in two Game Worlds.
The reloaded CDOs retain complete scalar/Array/Set/Map values and exact immediate
component archetypes. Ownership is assigned only to the base; native Actor
BeginPlay starts each inherited component from the verified fixture chunk.
Managed code consumes the configured state through generated bindings, drives
native Actor rotation, and flushes tick state back to reflected properties.
Each World reloads independently with retained state and runtime identity;
tearing down the first leaves the second active. Final teardown restores script,
lifecycle-claim and managed World counts, and the test unloads and deletes its
four owned assets. This reload uses the same chunk and tests state/configuration
retention; changed managed bodies were verified separately in C-2.

Evidence is under `artifacts/m13-3c-3-inherited-runtime`:

- `fixture-build-8.log` — managed build, official declaration/chunk emission and
  isolated publish succeed; the emitted stable identity sidecar is retained.
- `editor-build-inherited-state-7.log` — the final native test compiles and links.
- `inherited-state-cmd-5-ue.log` — `InheritedActorStateRuntime` reports Success
  and `Automation Test Queue Empty 1 tests performed`, with actual chunk startup
  in both Worlds and no test-event errors. Existing startup self-check messages
  precede the selected test and are not counted as its failures.

Only the builder's GUID/package case checks and generated identity sidecar were
updated after that run; the tested C#/native runtime behavior is unchanged.
Broader managed-class/asset-operation identity coverage remains open, so C-3 is
not complete.

The cooked path now retains those four GUID-named packages only after the
Editor runtime assertions and package unload succeed. The driver stages the
fixture chunk, assembly and declaration under
`Binaries/Managed/InheritedState/<fixtureId>`; the saved Script Asset uses
`{PluginDir}` paths. The existing Runtime module stages these files, while the
receipt identifies the exact four packages and generated class paths.
`-UseExistingFixture` consumes the retained stage without replacing its programs.

The Win64 Development archive passes the native Host verifier. It loads the
cooked base/ordinary child/grandchild classes and exact sidecar, checks complete
defaults and immediate component archetypes, and starts the archived chunk in
two Game Worlds. Each World retains its scalar/Array/Set/Map state and runtime
identity through reload. After the first Actor and World are destroyed, the
second continues ticking and completes its own reload. Script, lifecycle-claim
and managed World counts return to their initial values. The verifier checks
that both the program and actual loaded hostfxr belong to the executing archive.

Cooked evidence is under `artifacts/m13-3c-3-inherited-cook`:

- `editor-build-cooked-inherited-state-5.log` compiles the fixture changes;
  `fixture/editor-fixture-generation.log` reports `InheritedActorStateRuntime`
  Success and `Automation Test Queue Empty 1 tests performed` with portable paths.
- `fixture/packaged-build-cook.log` builds the final Host verifier for Editor and
  Game, then completes Win64 Development cook, stage and archive with exit 0.
- `fixture/packaged-inherited-state.log` and
  `fixture/inherited-state-cook-result.txt` record the archived executable's PASS,
  actual chunk startup in both Worlds and archive-local hostfxr.
- `fixture/build-cook-run.log` records successful execution and deletion of the
  four owned source `.uasset` files and three staged program files. The archive
  remains at `package/Windows/UEUnifiedScriptHost`; shared host/Demo inputs were
  not replaced or removed.

The separate `InheritedClassAssetOperations` regression uses the real Actor
factory and its normal source identity form. The base owns ordinary native
Blueprint scalar/Array/Set/Map user variables; its child owns complete value
overrides and explicitly disables automatic start/tick, and a grandchild follows
that child. These native user variables use names distinct from the factory's
managed properties.
The test saves packages and scans their real Asset Registry dependencies before
calling the existing complete-class Rename/Duplicate/Delete transactions.

Dirty child referencers reject Rename before mutation. The rename publication
failure seam restores source/deployment/first-play bytes, package dirty flags
and the inherited hierarchy. Clean Rename retains ClassId and variable GUIDs,
repairs the child referencer, removes the old package files, and survives
package unload/reload with exact class/sidecar identity and complete defaults.
Duplicate creates a separate ClassId and sidecar; its new ordinary child and
grandchild inherit the copied base values and automatic component settings.
Failure injection with both branches present preserves their configuration,
dirty boundaries and both managed source files. Native Actors spawned from
the saved/reloaded branches retain their respective full state and component
name, outer, immediate archetype, sidecar and start/tick settings. Delete rejects
the duplicated class while ordinary descendants reference it.

No production asset-operation repair was required. An early Map fixture used
an invalid text import form, and the initial grandchild was created before its
parent's complete overrides were established. Later instance checks used an
original-chain pointer after the duplicate unload/GC pass had collected it.
The final fixture establishes the child values before creating its follower
and reacquires both branches by saved paths after GC. These failures do not
establish product inheritance or rename defects.

Both asset-operation tests arm shared-file restoration only after successful
snapshots; restoration failures are reported to Automation. The new fixture
unloads its owned packages and deletes their source/asset files, then removes
only its empty package directory. This lane spawns native Actors without
BeginPlay of the factory's uncompiled managed source. Active managed
rename/rebuild/reload coherence remains unverified by this lane; the earlier
isolated managed runtime/cooked fixture does not supply that evidence either.

Asset-operation evidence is under `artifacts/m13-3c-3-inherited-asset-ops`:

- `editor-build-17.log` compiles both final test files and links the HostEditor.
- `inherited-class-asset-ops-cmd-17-runtime.log` reports
  `InheritedClassAssetOperations` Success and Queue Empty with one test performed.
- `class-asset-operations-cmd-2-runtime.log` reports the existing `AssetOperations`
  regression Success and Queue Empty with one test performed.

General managed-base state remains a distinct contract gap: `LowerStates` in
`CSharpScriptAssetLowerer.cs` enumerates only the current type's members, and
`UnrealScriptStateSynchronizer.StateContract.Create` likewise uses
`BindingFlags.DeclaredOnly`. The inherited public visual-method test does not
cover inherited auto-properties. Before extending this scope, define matching
declaration/runtime rules for state ownership, stable IDs, member hiding and
generated native paths; ordinary Blueprint inheritance does not prove them.

The next state-contract change must retain the declaring property's identity:
`CSharpIdentityReconciler` records a state under its declaring type, and the
same Roslyn property may appear in several derived assets. Keep exposure
selection local to each declaring type instead of treating the concrete
`ScriptAsset` attribute as permission to export every framework-base property.
Effective defaults must account for base construction and subsequent derived
constructor assignments; changing member enumeration alone would miss them.
Resolve ambiguous hiding before publication and keep runtime bindings on the
concrete generated class path. This managed-property contract does not imply
native Blueprint parent materialization: the current materializer rejects a
new state that masks an existing native parent property.

Include declaration editing in that change. `CSharpDeclarationPatchApplier`
currently flattens all asset states into a dictionary keyed only by state ID,
both before editing and during postcondition checks. Inherited projections
would repeat the declaring ID and may carry different effective defaults.
Resolve the source declaration owner explicitly and validate its affected
projections rather than arbitrarily selecting the first asset or assigning a
new ID to each derived appearance.

## Relevant verification

Use focused CMD tests with a managed Actor base, an ordinary Blueprint child
and grandchild, and instances in two Worlds. Check the configuration producer,
native component identity, actual managed startup, inherited defaults and
intentional overrides before/after compile and base synchronization. Include
failure preservation and teardown. Once the selected behavior works, carry it
through package save/load and the affected cooked path before closing the
batch. Existing C-2 component-class cooked inheritance evidence does not prove
Actor native-subobject inheritance.
