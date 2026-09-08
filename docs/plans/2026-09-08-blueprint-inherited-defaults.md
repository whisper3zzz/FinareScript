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
Inherited managed startup/reload and cooked execution remain outstanding.

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

The additional `CommonStateMaterialization` run fails during managed activation:
`CreateScriptWithBlueprintContract` cannot load `UnifiedScript.Unreal, Version=1.0.0.0`.
See `common-state-cmd-final.log`. A read-only decode of the staged Demo chunk's
DependencyManifest confirms that all six shared managed dependency SHA256 values
differ from the DLLs in `Binaries/Managed`. The Unreal DLL exists; its digest is
`e4ac8a41292b1c81b01a90acdbf6f99bcf1d263bf27eb64193e426e48c25c6c4`, while the
chunk requires `1325cd60de53b2e814fb1962de86f2dad472e5e65c9069b3f83bed62600b992e`.
The chunk load context verifies shared assemblies through
`UnifiedScriptManagedDependencyCache.Verify`, which rejects differing digests.
Rebuild and emit matching deployment artifacts before rerunning this test; do
not change the manifest to bypass verification. The subsequent managed behavior
assertions are not passing evidence. The native checkpoint changes no managed
assembly or deployment and does not claim managed startup/reload coverage.

## Relevant verification

Use focused CMD tests with a managed Actor base, an ordinary Blueprint child
and grandchild, and instances in two Worlds. Check the configuration producer,
native component identity, actual managed startup, inherited defaults and
intentional overrides before/after compile and base synchronization. Include
failure preservation and teardown. Once the selected behavior works, carry it
through package save/load and the affected cooked path before closing the
batch. Existing C-2 component-class cooked inheritance evidence does not prove
Actor native-subobject inheritance.
