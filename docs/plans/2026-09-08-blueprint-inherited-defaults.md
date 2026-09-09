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

The managed-state checkpoint replaces leaf-only enumeration in
`CSharpScriptAssetLowerer` and `UnrealScriptStateSynchronizer` with matching
declaring-type rules. Each type's own `ScriptAsset` exposes its instance
auto-properties; plain helpers contribute only explicit `Expose` properties.
Unexposed helper/framework properties remain outside the contract. The declaring
property retains its sidecar identity and source span in every projection.
JSON and compiled chunk identity validation remains scoped per asset; shared
projections do not require a runtime serialization version change.

Effective defaults follow reviewed parameterless base-to-leaf constructor
assignments. Conditional/deferred/compound writes and return/goto paths that
can bypass the selected assignment are rejected. This is not an arbitrary C#
constructor interpreter. Ambiguous hiding is rejected before publication, with
case-insensitive Unreal names. Runtime bindings use the concrete generated
class path, Actor Target and component Host. CoreCLR receiver overrides resolve
eligible private base properties and nullable reference metadata before a
concrete leaf's own Visual method executes. Inherited-method receiver dispatch
remains separate work. Native Blueprint parent materialization is not inferred
from C# inheritance; a new state masking a native parent property is rejected.

Declaration patches resolve the Roslyn source owner of shared state/function
IDs, including partial source files, and check that owner's precondition and
postcondition. Updating a default changes its effective owner constructor
assignment when present, preserving earlier assignments, initializers and
derived overrides. Function identities are persisted in sidecars so declaration
patch renames retain the shared ID and repair callers. Semantic identity selection uses the
actual UnifiedScript attributes, including attributes on another partial file;
the existing syntax-only reconciler overload remains available. Failed edits
publish no partial source or sidecar.

The Release managed check passed 224 tests across
`CSharpScriptAssetLowererTests`, `CSharpDeclarationPatchApplierTests`,
`CSharpIdentityReconcilerTests`, `CSharpSourcePatchApplierTests`,
`CoreClrExecutionHostTests`, `UnrealScriptObjectModelTests` and
`InheritedStateContractTests`. It used the bundled UE SDK's `dotnet test` with
`--no-restore -p:UnifiedScriptSkipManagedHostStaging=true` and a combined
FullyQualifiedName filter for those classes. This result is recorded in tool
stdout; no log/TRX was saved. Coverage includes shared-ID JSON/binary roundtrip,
cross-file source-owner edits, derived defaults, full collection lifecycle on
Actor/Component hosts, runtime hiding rejection and private/nullable CoreCLR
state overrides. The subsequent UE runtime and cooked proof is recorded below.

Review found two integration defects in the initial implementation. Multiple
constructor default edits could invalidate later absolute syntax spans; the
patch now annotates every final assignment before renaming references or
replacing values. The focused patch checks passed 133 tests in
`artifacts/m13-3c-3-managed-base-state/managed-patch-batch.log`.

The first function identity implementation changed only declaration IDs, which
left HIR/Graph and CoreCLR on different method IDs. Persistent IDs now enter at
HIR reconciliation, before semantic binding; declarations, callers, Graph and
execution share that ID without a consumer fallback. Old sidecars without
function entries retain available canonical method IDs on upgrade. Arbitrary
text renames without identity pins do not receive a new cross-owner guessing
rule. The supported rename proof uses the actual declaration patch and its
identity pins, then projects Authoring and executes the renamed program.

The final identity integration regression passed 194 tests in
`artifacts/m13-3c-3-managed-base-state/managed-function-identities-3.log`.
It uses the same Release/no-restore/staging-disabled test command with a
FullyQualifiedName filter for `FunctionIdentityPipelineTests`,
`CSharpIdentityReconcilerTests`, `CSharpDeclarationPatchApplierTests`,
`CSharpSourcePatchApplierTests`, `AuthoringSurfaceProjectorTests`,
`CoreClrExecutionHostTests`, `CSharpProgramEmitterTests`,
`CompiledProgramChunkTests` and `CompiledChunkTests`. The initial failing
identity integration run remains in `managed-function-identities.log`;
`managed-function-identities-2.log` records the earlier 148-test passing run.
The final two pipeline tests were rerun after strengthening the canonical-ID
upgrade and renamed caller-binding assertions; both pass in
`managed-function-identities-final.log`. No product code changed after the
194-test regression.

The native proof distributes the existing isolated inherited-state fixture
across source-defined base, middle and concrete classes. It preserves final
scalar and complete Array/Set/Map defaults through deterministic constructor
assignments, including leaf overrides of different base defaults. It explicitly
checks that an unexposed helper property does not become a native field. Its
ordinary Blueprint child/grandchild, two-World startup/reload and cooked verifier
exercise the projected managed state. The changed managed host and CLI are
built coherently before fixture emission; routine managed checks keep host
staging disabled until that deliberate deployment step. This source change
requires new runtime evidence, unlike the earlier native-only asset-operation
checkpoint.
`artifacts/m13-3c-3-managed-base-state/editor-build.log` records successful
HostEditor compilation; the runtime and cooked results follow below.

The managed checkpoint is committed as `9310b22`. The official Release
CLI/host/Demo build and emit completed with coherent staged dependencies;
`managed-release-deploy.log` records it. Generated Demo sidecars add 24 function
entries while retaining every existing entry ID. The fixture's five state
declarations now belong to its source base/middle types, so its regenerated
sidecar replaces the old leaf-owned fixture IDs through the normal emitter.

The first `InheritedActorStateRuntime` run with this source hierarchy passed
Success and Queue Empty with one test in `fixture/editor-fixture-generation.log`.
Its subsequent Cook reached staging but failed because the cached Game target
receipt still named the already-cleaned earlier fixture
`D73C2073901A47EC9E165784FDAA0979`. The driver cleaned its new four packages and
three staged program files as designed. This was a stale UBT runtime-dependency
list, not a failure of the new two-World state behavior. The first-attempt cook
and driver logs are retained under the fixture artifact directory.
The driver now passes `-UbtArgs=-NoUBTMakefiles` to BuildCookRun so each dynamic
fixture's current staged dependencies are gathered. It does not fabricate old
files or edit build receipts. The retry regenerates the cleaned source fixture
before repeating the affected packaging path.

The retry passed with fixture `5240D159D0484FA8BB34D15938E42465`, using the
managed source/deployment from `9310b22`. Evidence is under
`artifacts/m13-3c-3-managed-base-state`:

- `fixture/editor-fixture-generation.log`: `InheritedActorStateRuntime` Success
  and Queue Empty with one test. Saved/reloaded ordinary Blueprint descendants
  start the projected managed state in two Worlds, retain full values and
  identity through independent reloads, and restore lifecycle/World counts.
- `fixture/packaged-build-cook.log`: UAT BuildCookRun succeeds with exit 0 after
  collecting current runtime dependencies.
- `fixture/packaged-inherited-state.log` and
  `fixture/inherited-state-cook-result.txt`: the archived verifier passes the
  same complete state, immediate component archetypes, reload and teardown
  contract. It records the actual archive-local chunk and bundled hostfxr.
- `fixture/build-cook-run.log`: PASS and exact cleanup of this fixture's four
  source packages and three staged program files. The archive remains at
  `package/Windows/UEUnifiedScriptHost`.

Active managed Rename/Duplicate followed by rebuild/reload remains open and
prevents closing C-3 as a whole. The earlier asset-operation checkpoint uses
native instances without starting its managed source. The authoring ownership
checkpoint below addresses a separate defect: shared declaration IDs previously
selected edit preconditions from the first asset and navigation rows from the
last asset, while the readers rejected shared function projections entirely.

The selected authoring contract is Surface v0.5 with explicit
`declarationOwners` entries (`declarationId`, `ownerAssetId`). Analysis exposes
the same mapping without depending on Authoring; CLI and debug-server
projection boundaries pass it into the engine-neutral surface. Owners come
from Roslyn declaration symbols and resolved asset IDs, including explicit
ClassId and partial types. Runtime declaration and chunk schemas stay unchanged.
The managed and native readers retain v0.4 support; managed legacy roundtrip
preserves that version.
Unique legacy declarations remain editable; ambiguous legacy shared state
remains displayable without an editable owner. New shared projections require
an explicit owner and coherent contracts. Derived state defaults may differ;
shared function contents must agree except for the projection's qualified name.

Native state/function lookup, patch preconditions and semantic navigation now
use the source owner. Only owner rows populate the semantic index, including
the shared actions and regions of inherited functions; derived rows retain
their effective defaults. Surface parsing validates a candidate before replacing
the loaded document. The lowerer also projects inherited functions onto assets
with no methods of their own, preserving the source method's persistent ID.

The browser retains effective state drafts on the selected derived asset and
offers explicit navigation to edit the source owner. Pending drafts block that
navigation. Only declarations owned by the selected asset can produce update/
removal operations; untouched inherited rows do not block local declarations.
Unsupported editor types remain read-only and retain their typed JSON for
display. Debug requests send only edited supported overrides; unchanged values
come from the selected receiver's constructor. This also avoids rewriting wide
integers through JavaScript's lossy JSON number representation. The workspace
still exposes one primary source text, even
when compilation includes several files; spans from other files display their
real location without highlighting unrelated primary-source lines.

CoreCLR resolves the requested receiver asset, checks that it projects the
invoked method and that its runtime type derives from the method's declaring
type, then constructs the selected type. State overrides use that asset's
contract and actual hierarchy. The declaring base is never substituted for a
selected leaf receiver. Static methods still reject instance receivers.

The producer/native/CLI declaration roundtrip now has direct evidence under
`artifacts/m13-3c-3-authoring-ownership/`. The official single-file CLI analysis
publishes AOverride first with default 77, BFollower with 3 and the explicit
ZBase owner with 3; all three project the same two Visual functions. The real
Slate widget exports a source-owner patch that renames Seed to RenamedSeed and
changes the owner default to 11. CLI apply plus reanalysis preserves the shared
state ID, both function IDs and explicit owner ID; BFollower/ZBase become 11
while AOverride remains 77. See `native-cli-roundtrip-result.json`,
`native-cli-apply.log` and `native-cli-reanalysis.log`.

This path exposed a native reader defect: UE's JSON writer cannot serialize a
primitive as the document root, and the old fallback silently displayed it as
null. State defaults now serialize through a one-element array using condensed
JSON; missing values or serialization failure are errors, while explicit null
remains a valid value. Another verification defect hid the existing Workbench
Automation test when its name became a parent of other tests. UE's report
enumerator only schedules leaf nodes, so its name is now the Workbench child
of the same AuthoringSurface prefix.

The final native ownership test passes in `native-authoring-3.log` with Success
and Queue Empty (one test), after `editor-build-5.log` compiled the final test.
The preceding `native-authoring-2.log` passes Workbench, ScalarDefaultRoundtrip,
StructDefaultPrecondition and V05RejectsWithinAssetDuplicateFunction. Its only
failed test asserted that a root region was a visible sidebar child; root
regions are indexed but intentionally omitted from that tree. The corrected
test checks that constructing a derived projection cannot replace the owner
function/region/action index entries. The CLI roundtrip inputs and product code
did not change after that earlier run.

The browser's live workflow also passes against a separate artifact-directory
copy of the fixture. AOverride's inherited Forward with the default argument 12
returns 89. Its Seed inspector shows effective 77 and a read-only declaration;
source-owner navigation selects Game/ZBase and shows 3. The actual Commit Patch
and reviewed-source confirmation publish 11 in revision 2 / CoreCLR generation
2. BFollower then returns 23 while AOverride still returns 89; both retain the
typed Values array `[2,4]`. A pending local ArmorRatio declaration blocks owner
navigation and remains in the browser draft. No checked-in fixture source was
edited by this interaction. `browser-workspace-after.json` captures the committed
server workspace; the isolated source and transaction journal are retained in
`browser-workspace/`. This is browser interaction evidence, not a physical Slate
toolbar or cooked-runtime claim.

The final browser request filter was checked again with the same committed
fixture: constructor-backed AOverride/BFollower execution returns 89/23 without
resending unchanged state. Five Node regressions pass in
`visual-authoring-behavior-final-4.log`, exercising the actual validator,
declaration patch builder and debug request function through a VM harness.
The focused managed results are 12 CoreCLR tests in
`coreclr-inherited-receiver-2.log`, the inherited HTTP workspace/debug test in
`coreclr-inherited-http-3.log`, and four ownership tests including an actual
graph-content mismatch in `authoring-ownership-graph-negative.log`. The earlier
111-test regression is retained in `managed-existing-regression.log`; it predates
the selected-receiver change and is not a rerun of that later code.

Repository validation stops at the existing API Contract/visual catalog
consistency assertion (`repository-validation.log`), before the changed Surface
schema check. That full validator is not reported as passing. No unrelated API
generation, host staging or protected-file replacement was performed for this
authoring checkpoint.

## Active managed asset operations (in progress)

The first running factory fixture reaches declaration synchronization, managed
startup in two Worlds, and the real Rename transaction. Its second CMD run
exposes a runtime defect: old generations hydrate `DisplayText` during teardown
using the original generated class path, which no longer resolves after Rename.
Both Worlds report `TargetTypeNotFound` in
`artifacts/m13-3c-3-active-asset-operations/active-cmd-2.log`.
This is not a passing asset-operation checkpoint. The fixture also needs to
resolve generated function names through their binding metadata and carry the
source-adjacent identity sidecars through Rename before rebuilt execution can
be evaluated.

The repair uses an internal `unifiedscript-class/us_<id>:<member>` locator when
the selected managed Script Class declares a valid, nonzero persistent ClassId.
Native resolution must match that ID against the exact target's generated-class
ancestor chain and its retained Script Asset. It must not introduce global old
path aliases or infer ownership from names or arbitrary components. State
projection and RPC dispatch use the selected leaf identity; legacy declarations
without an explicit ClassId retain their existing class-path route. Invalid
explicit IDs fail rather than silently choosing a different identity. Tests
must cover old-generation state access after Rename, rejected candidates,
successful rebuilt bodies, duplicate isolation and lifecycle cleanup before
this work is considered complete.

The stable-locator implementation passes 22 focused managed tests in
`managed-stable-identity-3.log`, including real lifecycle state synchronization
for different selected managed types/paths sharing one ClassId and the RPC
target/argument contract. `editor-stable-locator-build-2.log` builds the native
consumer and its rejection test; `native-stable-locator-negative.log` reports
Success and Queue Empty for `StableMemberLocatorValidation`. These checks do
not yet establish successful active Rename/Duplicate rebuilding and reload.

`active-cmd-4.log` now starts the real factory chunk in two Worlds and reaches
the renamed rebuild. The initial state Tick, native AddOne call, injected
Rename rollback and Rename-before-rebuild Tick/call assertions report no
failures. Both Worlds tear down without the old generated-class-path bridge
error. The whole selector still fails: the renamed C# DLL compiles, then the
builder rejects the missing adjacent sidecar rather than reinitializing its
IDs (`A9D167A749170E318A991CBDBFEBA113/fixture-build-renamed.log`). No renamed
reload or Duplicate runs in this check. The initial fixture builder now uses
official `identity-init` before `emit`; `emit` alone does not create a sidecar
for a source that has none. The final native fixture builds in
`editor-active-fixture-build-3.log`, and its configuration restore data comes
only from the initial successful snapshot.

Explicit Blueprint override reload remains a separate check within this slice:
generation compatibility currently compares the full managed method identity,
including its declaring type name. A class rename must retain the same explicit
declaring-owner ClassId and method contract; it must not relax method-renaming
or owner-change rejection merely because the native route still matches.

Source identity migration is also required: asset operations currently snapshot
and publish C# sources, deployment and first-play metadata, but omit adjacent
`.cs.usids` files. Moving only the sidecar and changing `sourcePath` would still
lose IDs because reconciliation matches qualified declaration owners. The
managed transition planner must analyze the current source with its existing
sidecar, map unchanged syntax structure onto the complete final source, pin
each live identity to its new span, and let the semantic pipeline regenerate
owners, fingerprints, anchors and source digests. Rename preserves IDs;
Duplicate initializes independent IDs for its own declarations while retaining
external inherited declaration ownership. Missing legacy sidecars remain
missing; ambiguous mappings and structural changes fail before publication.
The native consumer must include old/new sidecar paths in collision checks,
snapshots, concurrent-change checks, journal recovery and rollback. This
producer/consumer work remains unfinished.
Duplicate must also apply Roslyn edits to the copied declaration's constructor
and self references: the current native text transform changes only the class
declaration, while the factory now emits an explicit constructor. Original
source and external consumers must retain their original target. For partial
types, copy every source containing a bound declaration of that type, with
distinct destination paths and sidecar transitions; copying only the primary
file can silently omit methods and state. Validate the complete candidate
compilation before mutation, including conflicts caused by unrelated types in
those copied files.

The memory-only transition planner passes 11 focused tests in
`artifacts/m13-3c-3-active-asset-operations/transition-planner-review-fix.trx`.
They cover stale sidecars, repeated graph nodes, a real multi-file partial-type
rename with in-place reference edits, and independent duplicate declarations
that retain an external base's shared state identity. Unchanged duplicate type
names, colliding destinations and syntax topology changes are rejected. This
evidence covers the managed transformation only; the CLI boundary and native
transaction are still being connected.

## Relevant verification

Use focused CMD tests with a managed Actor base, an ordinary Blueprint child
and grandchild, and instances in two Worlds. Check the configuration producer,
native component identity, actual managed startup, inherited defaults and
intentional overrides before/after compile and base synchronization. Include
failure preservation and teardown. Once the selected behavior works, carry it
through package save/load and the affected cooked path before closing the
batch. Existing C-2 component-class cooked inheritance evidence does not prove
Actor native-subobject inheritance.
